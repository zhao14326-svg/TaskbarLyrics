using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using TaskbarLyrics.Models;

namespace TaskbarLyrics.Services;
/// <summary>
/// 歌词管理：按优先级获取歌词
/// 1. 本地 .lrc 文件（按歌名在音乐库中匹配）
/// 2. 音频文件内嵌歌词（MP3 ID3v2 / FLAC Vorbis）
/// 3. 播放器缓存目录（QQ/网易云/酷狗共享缓存）
/// 4. 在线歌词（lrclib.net）
/// </summary>
public partial class LyricsManager : ILyricsProvider
{
    private static readonly string[] AudioExtensions = [".mp3", ".flac", ".m4a", ".ogg", ".wma", ".ape", ".wav"];
    public static readonly string[] DefaultMusicFolders;

    private readonly IOnlineLyricsService _onlineService;
    private readonly IPlayerLocalApiService _localApi;
    private readonly ILyricsCache _cache;
    private readonly IAudioTagLyricsReader _tagReader;
    private readonly List<string> _audioFiles = new();
    private readonly object _indexLock = new();
    private bool _indexReady;

    public LyricsManager(IOnlineLyricsService onlineLyrics, IPlayerLyricsCache playerCache,
        IPlayerLocalApiService localApi, ILyricsCache cache, IAudioTagLyricsReader tagReader)
    {
        _onlineService = onlineLyrics;
        PlayerCache = playerCache;
        _localApi = localApi;
        _cache = cache;
        _tagReader = tagReader;
    }

    // 并发与缓存状态
    private string _currentKey = "";          // 最近一次请求的曲目
    private string _latestSetKey = "";        // 当前 Current 对应的曲目
    private readonly ConcurrentDictionary<string, Task<LyricsData?>> _inflight = new();
    private readonly ConcurrentDictionary<string, DateTime> _negativeCache = new();
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);

    // 两阶段渐进加载：阶段1（本地/缓存，毫秒级）→ 阶段2（在线，后台带超时）
    private static readonly TimeSpan FastStageTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan OnlineStageTimeout = TimeSpan.FromMilliseconds(2500);
    // 最近成功联网获取过的曲目（避免本地命中后仍反复联网升级）
    private readonly ConcurrentDictionary<string, DateTime> _onlineFetched = new();
    private static readonly TimeSpan OnlineFetchCooldown = TimeSpan.FromHours(6);

    // 最近成功获取的歌词（完整内存对象保留）：切回同歌零重新解析/联网，
    // 避免 ABAB 来回切歌时歌词被反复重置下发导致显示跳动
    private readonly ConcurrentDictionary<string, LyricsData> _recentLyrics = new();
    private const int RecentLyricsCap = 40;

    static LyricsManager()
    {
        var folders = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var root = drive.RootDirectory.FullName;
            folders.Add(Path.Combine(root, "Music"));
            folders.Add(Path.Combine(root, "音乐"));
            folders.Add(Path.Combine(root, "Downloads"));
            folders.Add(Path.Combine(root, "BaiduNetdiskDownload"));
            folders.Add(Path.Combine(root, "CloudMusic"));
            folders.Add(Path.Combine(root, "qq", "Tencent Files"));
            folders.Add(Path.Combine(root, "迅雷下载"));
            folders.Add(Path.Combine(root, "夸克网盘"));
            folders.Add(Path.Combine(root, "kuake"));
        }
        DefaultMusicFolders = folders.Where(Directory.Exists).ToArray();
    }

    /// <summary>音乐目录（用于查找本地内嵌歌词）</summary>
    public string[] MusicFolders { get; set; } = DefaultMusicFolders;

    /// <summary>是否启用在线歌词获取</summary>
    public bool EnableOnline { get; set; } = true;

    /// <summary>播放器缓存共享</summary>
    public IPlayerLyricsCache PlayerCache { get; set; } = null!;

    /// <summary>已索引的音频文件列表（供 CoverArtService 复用）</summary>
    public IReadOnlyList<string> AudioFiles => _audioFiles;

    /// <summary>当前是否显示波形动画（无歌词时）</summary>
    public bool IsInstrumental { get; set; }

    /// <summary>当前歌词</summary>
    public LyricsData? Current { get; set; }

    /// <summary>
    /// 根据曲目信息获取歌词（带缓存 + 两阶段渐进加载）。
    /// 阶段1（快速，≤1.2s）：内存/持久缓存 → 本地API → 本地文件 → 播放器缓存，命中立即返回；
    /// 阶段2（在线，≤2.5s）：仅当快速阶段无结果时等待在线检索；
    /// 快速阶段已命中时，在线阶段作为“升级”（翻译/更优同步）在后台运行，完成后自动发布到 Current，
    /// UI 下一 tick 拾取并重新下发，无需再次等待。
    /// </summary>
    public async Task<LyricsData?> GetLyricsAsync(MediaTrack track)
    {
        var key = Normalize($"{track.Title}|{track.Artist}");
        if (key.Length == 0)
        {
            Current = null;
            IsInstrumental = true;
            return null;
        }

        _currentKey = key;

        // 同一首歌已获取成功且内存中仍持有歌词：直接返回。
        // 注意：悬浮窗换歌时会清空 Current，若 _latestSetKey 仍指向该歌，必须重新走获取流程
        // （内存缓存会瞬间命中恢复），否则会误判为“无歌词”导致只剩频谱。
        if (key == _latestSetKey && Current is { IsEmpty: false })
            return Current;

        // 最近成功获取过的曲目直接命中完整内存对象（即使切歌清空了 Current，
        // ABAB 来回切歌也无需重新解析缓存/触发在线检索，避免歌词行反复重置跳动）
        if (_recentLyrics.TryGetValue(key, out var recent))
        {
            if (key == _currentKey && Current is null)
            {
                Current = recent;
                IsInstrumental = false;
                _latestSetKey = key;
            }
            return recent;
        }

        // 最近一次获取失败（负缓存）：跳过网络请求
        if (_negativeCache.TryGetValue(key, out var failAt) &&
            DateTime.UtcNow - failAt < NegativeCacheTtl)
            return Current;

        // 合并并发重复请求（定时器 tick 可能重叠，避免同一首歌多次联网）
        var task = _inflight.GetOrAdd(key, _ => FetchInternalAsync(track, key));

        // 阶段1（快速）：等本地来源完成，命中立即显示（通常 <300ms，本地/缓存歌曲不再等待在线）
        var result = await AwaitWithTimeoutAsync(task, FastStageTimeout);
        if (result is { IsEmpty: false })
        {
            // 在线“升级”阶段继续在后台运行；任务完成后移除去重条目
            _ = task.ContinueWith(t => { _inflight.TryRemove(key, out _); }, TaskScheduler.Default);
            return result;
        }

        // 阶段2（在线）：等待完整任务（含在线检索），最多 OnlineStageTimeout；
        // 超时后任务仍在后台运行，完成后通过 Publish 自动更新 Current（UI 下一 tick 显示）
        result = await AwaitWithTimeoutAsync(task, OnlineStageTimeout);
        bool completed = task.IsCompleted;
        _inflight.TryRemove(key, out _);

        // 负缓存：任务已确认失败直接记；仍超时未决的，由后台完成时补记（避免无歌词歌曲重复等待）
        if (result == null)
        {
            if (completed)
                _negativeCache[key] = DateTime.UtcNow;
            else
                _ = task.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result == null)
                        _negativeCache[key] = DateTime.UtcNow;
                }, TaskScheduler.Default);
        }
        return result;
    }

    /// <summary>等待任务最多 timeout；超时返回 null（任务继续在后台运行）。</summary>
    private static async Task<LyricsData?> AwaitWithTimeoutAsync(Task<LyricsData?> task, TimeSpan timeout)
    {
        if (task.IsCompleted) return await task;
        var done = await Task.WhenAny(task, Task.Delay(timeout));
        return done == task ? await task : null;
    }

    private async Task<LyricsData?> FetchInternalAsync(MediaTrack track, string key)
    {
        // 1. 持久化缓存（SQLite → 内存，7 天 TTL）
        var cachedEntry = _cache.TryGet(track.Title, track.Artist);
        if (!string.IsNullOrEmpty(cachedEntry.Text))
        {
            // 缓存时长为 0 时(SMTC 不可靠环境)从本地音频文件读取，保证纯文本估算有效
            var cachedDur = cachedEntry.DurationSec > 0 ? cachedEntry.DurationSec : await ResolveDurationSecAsync(track);
            var cached = LrcParser.ParseLyrics(cachedEntry.Text,
                cachedDur > 0 ? LrcParser.EstimatePlainInterval(cachedDur, cachedEntry.Text) : 0);
            if (cached.Lines.Count > 0)
            {
                cached.Source = "缓存(本地)";
                Publish(key, cached);
                return cached;
            }
        }

        // 2. 快速来源（毫秒级，无网络等待）：本地API → 本地文件 → 播放器缓存，谁先命中用谁
        var fast = await FirstSuccessAsync(FetchFromFastSourcesAsync(track));
        if (fast is { IsEmpty: false })
        {
            fast = await ReestimatePlainAsync(fast, track);
            _cache.Store(track.Title, track.Artist, fast.GetStoreText(), fast.Source, await ResolveDurationSecAsync(track));
            Publish(key, fast);
            // 3. 在线“升级”后台继续（可带来翻译/更优同步歌词）；近期已联网的曲目跳过
            if (!_onlineFetched.TryGetValue(key, out var ft) ||
                DateTime.UtcNow - ft > OnlineFetchCooldown)
                _ = RunOnlineStageAsync(track, key);
            return fast;
        }

        // 4. 在线来源（慢，各请求自带超时）
        var online = await FetchFromOnlineAsync(track);
        if (online is { IsEmpty: false })
        {
            online = await ReestimatePlainAsync(online, track);
            _cache.Store(track.Title, track.Artist, online.GetStoreText(), online.Source, await ResolveDurationSecAsync(track));
            _onlineFetched[key] = DateTime.UtcNow;
            Publish(key, online);
            return online;
        }

        // 全部来源失败：确认“无歌词”（IsInstrumental=true）
        Publish(key, null);
        return null;
    }

    /// <summary>
    /// 非同步（纯文本）歌词在播放器时长不可靠时，用音频文件真实时长重新估算间隔。
    /// 播放器缓存/在线歌词的 RawText 保留原文，可无损重新估算。
    /// </summary>
    private async Task<LyricsData> ReestimatePlainAsync(LyricsData data, MediaTrack track)
    {
        if (data.IsSynced || string.IsNullOrEmpty(data.RawText)) return data;
        var d = await ResolveDurationSecAsync(track);
        if (d <= 0) return data;
        var re = LrcParser.ParseLyrics(data.RawText, LrcParser.EstimatePlainInterval(d, data.RawText));
        if (re.Lines.Count == 0) return data;
        re.Source = data.Source;
        return re;
    }

    /// <summary>快速来源任务列表（无网络等待）：本地API → 本地文件 → 播放器缓存。</summary>
    private Task<LyricsData?>[] FetchFromFastSourcesAsync(MediaTrack track) =>
    [
        TryGetFromLocalApiAsync(track),
        FindLocalLyricsAsync(track),
        PlayerCache.TryGetLyricsAsync(track),
    ];

    /// <summary>在线来源（慢，各请求自带超时）。</summary>
    private Task<LyricsData?> FetchFromOnlineAsync(MediaTrack track) =>
        EnableOnline
            ? _onlineService.FetchAsync(track.Title, track.Artist, track.Album, track.Duration.TotalSeconds)
            : Task.FromResult<LyricsData?>(null);

    /// <summary>并行等待多个来源，谁先返回有效结果用谁。</summary>
    private static async Task<LyricsData?> FirstSuccessAsync(IReadOnlyList<Task<LyricsData?>> tasks)
    {
        var pending = new List<Task<LyricsData?>>(tasks);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            var result = await done;
            if (result is { IsEmpty: false }) return result;
        }
        return null;
    }

    /// <summary>后台在线歌词检索：成功后写入缓存，若仍是当前曲目则自动升级显示。</summary>
    private async Task RunOnlineStageAsync(MediaTrack track, string key)
    {
        try
        {
            var online = await FetchFromOnlineAsync(track);
            if (online is { IsEmpty: false })
            {
                _cache.Store(track.Title, track.Artist, online.GetStoreText(), online.Source, track.Duration.TotalSeconds);
                _onlineFetched[key] = DateTime.UtcNow;
                Publish(key, online);
            }
        }
        catch { }
    }

    /// <summary>若仍是当前播放曲目，则发布歌词结果（供 UI 下一 tick 拾取）。</summary>
    private void Publish(string key, LyricsData? data)
    {
        if (key == _currentKey)
        {
            Current = data;
            IsInstrumental = data == null;
            _latestSetKey = key;
        }
        if (data is { IsEmpty: false })
            Remember(key, data);
    }

    /// <summary>保留最近成功的歌词对象（上限容量，超出时粗裁剪最旧的若干条）。</summary>
    private void Remember(string key, LyricsData data)
    {
        _recentLyrics[key] = data;
        if (_recentLyrics.Count > RecentLyricsCap)
        {
            foreach (var k in _recentLyrics.Keys.Take(_recentLyrics.Count - RecentLyricsCap))
                _recentLyrics.TryRemove(k, out _);
        }
    }

    public string? GetCurrentLine(TimeSpan position) => Current?.GetLineAt(position)?.Text;
    public string? GetNextLine(TimeSpan position) => Current?.GetNextLineAt(position)?.Text;

    public double GetLineProgress(TimeSpan position)
    {
        if (Current == null || Current.Lines.Count == 0) return 0;
        var cur = Current.GetLineAt(position);
        if (cur == null) return 0;
        var next = Current.GetNextLineAt(position);
        if (next == null) return 0;
        var total = (next.Time - cur.Time).TotalMilliseconds;
        if (total <= 0) return 0;
        var elapsed = (position - cur.Time).TotalMilliseconds;
        return Math.Clamp(elapsed / total, 0, 1);
    }

    /// <summary>Find the best-matching audio file for a track. Checks filename first, then ID3 metadata.</summary>
    internal static string? FindBestAudioFile(MediaTrack track, IReadOnlyList<string> audioFiles, IAudioTagLyricsReader tagReader)
    {
        if (audioFiles.Count == 0) return null;
        var titleNorm = Normalize(track.Title);
        var artistNorm = Normalize(track.Artist);
        if (titleNorm.Length == 0) return null;

        // 元数据读取在 AudioMetaCache 命中时开销极小；为兼顾冷缓存与命中率,上限取 100。
        const int MaxMetadataReads = 100;
        int metadataReads = 0;

        var candidates = new List<(string Path, int Score)>();
        foreach (var file in audioFiles)
        {
            var name = Normalize(Path.GetFileNameWithoutExtension(file));
            if (name.Length == 0) continue;
            int score = 0;
            if (name.Contains(titleNorm, StringComparison.Ordinal))
                score = titleNorm.Length + (artistNorm.Length > 0 && name.Contains(artistNorm, StringComparison.Ordinal) ? artistNorm.Length : 0);
            else if (titleNorm.Contains(name, StringComparison.Ordinal) && name.Length >= 3)
                score = name.Length;

            // Boost score via ID3 metadata match
            if (score == 0 && metadataReads < MaxMetadataReads)
            {
                metadataReads++;
                try
                {
                    var meta = tagReader.ReadMetaCached(file);
                    var metaTitle = Normalize(meta.Title);
                    var metaArtist = Normalize(meta.Artist);
                    if (metaTitle.Length > 0 && metaTitle.Contains(titleNorm, StringComparison.Ordinal))
                        score = titleNorm.Length + (metaArtist.Contains(artistNorm, StringComparison.Ordinal) ? artistNorm.Length : 0);
                    else if (metaArtist.Length > 0 && metaArtist.Contains(artistNorm, StringComparison.Ordinal))
                        score = artistNorm.Length;
                }
                catch { }
            }

            if (score > 0) candidates.Add((file, score));
        }
        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        return candidates[0].Path;
    }

    // ==================== Local API lyrics (NetEase, instant) ====================

    private async Task<LyricsData?> TryGetFromLocalApiAsync(MediaTrack track)
    {
        try
        {
            var raw = await _localApi.GetLyricsAsync();
            if (raw != null && LrcParser.LooksLikeLrc(raw))
            {
                var data = LrcParser.Parse(raw);
                var (lrcTitle, _) = LrcParser.GetMetadata(raw);
                var titleNorm = Normalize(track.Title);
                var lrcTitleNorm = Normalize(lrcTitle);
                if (string.IsNullOrEmpty(lrcTitle) ||
                    lrcTitleNorm.Contains(titleNorm) || titleNorm.Contains(lrcTitleNorm))
                {
                    data.Source = "网易云本地";
                    return data;
                }
            }
        }
        catch { }
        return null;
    }

    // ==================== Local lyrics lookup ====================

    private async Task<LyricsData?> FindLocalLyricsAsync(MediaTrack track)
    {
        if (!_indexReady) return null; // 索引后台构建中：快速跳过，避免阻塞首次切歌（WarmUp/ResetIndex 负责构建）
        if (_audioFiles.Count == 0) return null;

        // 主候选：文件名 + ID3 元数据加权（元数据读取成本高，仅对首选执行）
        var best = FindBestAudioFile(track, _audioFiles, _tagReader);

        // 备用候选：按文件名打分取前 8，避免全量二次扫描
        var candidates = new List<string>();
        if (best != null) candidates.Add(best);
        foreach (var alt in _audioFiles
                     .Select(f => (Path: f, Score: ScoreFile(f, track)))
                     .Where(x => x.Score > 0)
                     .OrderByDescending(x => x.Score)
                     .Take(8))
        {
            if (!candidates.Contains(alt.Path)) candidates.Add(alt.Path);
        }

        // 播放器时长不可靠(0)时从音频文件读真实时长，保证纯文本歌词估算覆盖整首歌
        var resolvedDur = await ResolveDurationSecAsync(track, best);

        foreach (var path in candidates)
        {
            // 优先尝试同名 .lrc 文件（支持纯文本歌词）
            var lrcPath = Path.ChangeExtension(path, ".lrc");
            if (File.Exists(lrcPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(lrcPath);
                    var data = LrcParser.ParseLyrics(content,
                        resolvedDur > 0 ? LrcParser.EstimatePlainInterval(resolvedDur, content) : 0);
                    if (data.Lines.Count > 0)
                    {
                        data.Source = "本地LRC文件";
                        return data;
                    }
                }
                catch { }
            }

            // 再尝试内嵌歌词
            var embedded = await _tagReader.ReadFromFileAsync(path);
            if (embedded is { IsEmpty: false }) return embedded;
        }

        return null;
    }

    /// <summary>解析歌曲真实时长(秒)：SMTC/窗口时长不可靠(<=0)时，从匹配的本地音频文件读取（纯文本歌词估算用）。</summary>
    private async Task<double> ResolveDurationSecAsync(MediaTrack track, string? knownAudioFile = null)
    {
        if (track.Duration.TotalSeconds > 0) return track.Duration.TotalSeconds;
        var f = knownAudioFile ?? (_indexReady && _audioFiles.Count > 0 ? FindBestAudioFile(track, _audioFiles, _tagReader) : null);
        if (f == null) return 0;
        try { return await _tagReader.ReadDurationAsync(f); }
        catch { return 0; }
    }

    private static int ScoreFile(string filePath, MediaTrack track)
    {
        var name = Normalize(Path.GetFileNameWithoutExtension(filePath));
        var titleNorm = Normalize(track.Title);
        if (name.Length == 0 || titleNorm.Length == 0) return 0;
        if (name.Contains(titleNorm, StringComparison.Ordinal)) return titleNorm.Length;
        if (titleNorm.Contains(name, StringComparison.Ordinal) && name.Length >= 3) return name.Length;
        return 0;
    }

    // 音频索引总量上限（防止超大音乐库拖垮扫描与匹配）
    private const int IndexFileCap = 20000;

    private void EnsureIndex()
    {
        if (_indexReady) return;
        lock (_indexLock)
        {
            if (_indexReady) return;

            // 先加载缓存索引（毫秒级就绪，避免首次切歌等待扫描）
            var cachePath = Path.Combine(AppSettingsDir, "audio_index.json");
            try
            {
                if (File.Exists(cachePath))
                {
                    var json = File.ReadAllText(cachePath);
                    var cached = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                    if (cached != null && cached.Count > 0)
                    {
                        _audioFiles.AddRange(cached);
                        _indexReady = true;
                        // 每次启动后台重建索引：捕获新增/移动的音乐文件（不阻塞切歌）
                        _ = Task.Run(ScanAndSaveAsync);
                        return;
                    }
                }
            }
            catch { }

            // 无缓存：同步扫描（递归子目录，5s 超时）
            ScanAndSave();
            _indexReady = true;
        }
    }

    /// <summary>
    /// 递归扫描音乐文件夹（SearchOption.AllDirectories）建立音频索引，合并已有结果并保存缓存。
    /// 原实现仅扫顶层目录：嵌套文件夹（如 音乐/歌手/专辑/歌.mp3）中的本地歌词/内嵌歌词/封面全部匹配不到，
    /// 这是“获取不到歌词”的常见根因。
    /// </summary>
    private void ScanAndSave()
    {
        var results = new ConcurrentBag<string>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            Parallel.ForEach(MusicFolders, new ParallelOptions { CancellationToken = cts.Token }, folder =>
            {
                try
                {
                    var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                        .Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
                    foreach (var f in files)
                    {
                        if (results.Count >= IndexFileCap) break;
                        results.Add(f);
                    }
                }
                catch { }
            });
        }
        catch (OperationCanceledException) { }

        lock (_indexLock)
        {
            // 合并（去重）：保留已有索引，避免扫描超时/部分成功导致索引缩水
            var known = new HashSet<string>(_audioFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var f in results)
                if (known.Add(f))
                    _audioFiles.Add(f);
        }

        try
        {
            Directory.CreateDirectory(AppSettingsDir);
            var cachePath = Path.Combine(AppSettingsDir, "audio_index.json");
            File.WriteAllText(cachePath, JsonSerializer.Serialize(_audioFiles));
        }
        catch { }
    }

    private async Task ScanAndSaveAsync()
    {
        // 稍等，避免与切歌/歌词获取首帧争抢 IO
        await Task.Delay(1500);
        try { ScanAndSave(); }
        catch { }
    }

    private static string AppSettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarLyrics");

    public void ResetIndex()
    {
        lock (_indexLock)
        {
            _indexReady = false;
            _audioFiles.Clear();
            // Delete cached index so it rebuilds with new folders
            var cachePath = Path.Combine(AppSettingsDir, "audio_index.json");
            try { File.Delete(cachePath); } catch { }
        }
        // 保留当前已显示的歌词（Current/_latestSetKey），否则保存设置后当前歌会变成“无歌词”
        // 只清空失败缓存（允许换目录后重新尝试），并重建播放器缓存索引
        _negativeCache.Clear();
        PlayerCache.ResetIndex();
        // 立即后台重建索引，避免下一次切歌时跳过本地检索
        _ = Task.Run(EnsureIndex);
    }

    /// <summary>后台预热音频文件索引与播放器歌词缓存索引，减少首次切歌时的本地检索延迟。</summary>
    public void WarmUp()
    {
        _ = Task.Run(EnsureIndex);
        PlayerCache.WarmUp();
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex NonWordRegex();

    /// <summary>Normalize text for fuzzy matching. Removes symbols, lowercases.</summary>
    internal static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return NonWordRegex().Replace(text, "").ToLowerInvariant();
    }
}
