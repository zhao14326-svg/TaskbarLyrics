using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Media.Control;
using Windows.Storage.Streams;
using TaskbarLyrics.Helpers;

namespace TaskbarLyrics.Services;

public record MediaTrack(
    string Title, string Artist, string Album,
    string PlaybackApp, string PlaybackStatus,
    TimeSpan Position, TimeSpan Duration, byte[]? ThumbnailBytes);

/// <summary>
/// 双路检测 SMTC + 通用窗口标题扫描。
/// 窗口标题扫描覆盖所有包含 " - " 分隔符的进程（兼容网易云、QQ音乐、酷狗等）。
/// </summary>

public class SmtcMediaService : IMediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _smtcFailed;

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync()
    {
        if (_smtcFailed) return null;
        if (_manager != null) return _manager;
        try { _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { _smtcFailed = true; }
        return _manager;
    }

    /// <summary>SMTC first, window scan fallback.</summary>
    public async Task<MediaTrack?> GetCurrentTrackAsync()
    {
        var track = await GetFromSmtcAsync();
        return track ?? ScanWindowTitles();
    }

    /// <summary>SMTC only (no fallback).</summary>
    public async Task<MediaTrack?> GetFromSmtcOnlyAsync() => await GetFromSmtcAsync();

    private MediaTrack? _scanCache;
    private DateTime _scanCacheTime = DateTime.MinValue;
    private readonly object _scanLock = new();
    private volatile bool _scanRunning;

    /// <summary>
    /// Window title scan（500ms 缓存 + 后台执行：UI 线程永不阻塞）。
    /// 标题读取全部走 SendMessageTimeout(150ms)：即使某个窗口消息泵卡住，
    /// 单个窗口最多等待 150ms 且发生在后台线程，不影响 UI 线程与 250ms 定时器。
    /// </summary>
    public MediaTrack? ScanWindowTitles()
    {
        lock (_scanLock)
        {
            var now = DateTime.UtcNow;
            // 缓存有效期内直接返回（无需启动扫描）
            if (_scanCacheTime != DateTime.MinValue && (now - _scanCacheTime).TotalMilliseconds < 500)
                return _scanCache;
            // 后台扫描进行中：返回上次结果（最多滞后 ~500ms+扫描时间，检测可接受）
            if (_scanRunning) return _scanCache;
            _scanRunning = true;
            _scanCacheTime = now;
            _ = Task.Run(() =>
            {
                try { _scanCache = ScanAllWindows(); }
                finally { _scanRunning = false; }
            });
            return _scanCache;
        }
    }

    /// <summary>预热 SMTC 会话管理器（后台），使 PollSnapshot 能立即读到真实播放进度。</summary>
    public void WarmUp() { _ = GetManagerAsync(); }

    /// <summary>SMTC 实时播放快照（同步读取，无等待，用于每帧校准）。</summary>
    public sealed record SmtcSnapshot(string AppId, TimeSpan Position, TimeSpan EndTime, string Status);

    /// <summary>
    /// 同步读取媒体会话的实时播放状态。多实例播放器（多个 SMTC 会话）时，
    /// 遍历 GetSessions() 优先选“活跃”会话（position>0 或 Playing），
    /// 避免 GetCurrentSession() 返回陈旧/空会话导致校准失效。无活跃会话时回退当前会话。
    /// </summary>
    public SmtcSnapshot? PollSnapshot()
    {
        if (_smtcFailed || _manager == null) return null;
        try
        {
            GlobalSystemMediaTransportControlsSession? session = null;
            try
            {
                foreach (var s in _manager.GetSessions())
                {
                    try
                    {
                        var t = s.GetTimelineProperties();
                        var st = s.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "";
                        if (t != null && (t.Position > TimeSpan.Zero ||
                            st.Equals("Playing", StringComparison.OrdinalIgnoreCase)))
                        {
                            session = s;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            session ??= _manager.GetCurrentSession();
            if (session == null) return null;

            var tl = session.GetTimelineProperties();
            if (tl == null) return null;
            var status = session.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "";
            var appId = session.SourceAppUserModelId ?? "SMTC";
            return new SmtcSnapshot(appId, tl.Position, tl.EndTime, status);
        }
        catch { return null; }
    }

    // ==================== SMTC ====================

    private async Task<MediaTrack?> GetFromSmtcAsync()
    {
        try
        {
            var manager = await GetManagerAsync();
            if (manager == null) return null;
            var session = manager.GetCurrentSession();
            if (session == null) return null;
            var props = await session.TryGetMediaPropertiesAsync();
            if (string.IsNullOrEmpty(props?.Title)) return null;

            var status = session.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "Unknown";
            var position = TimeSpan.Zero;
            var duration = TimeSpan.Zero;
            try { var tl = session.GetTimelineProperties(); if (tl != null) { position = tl.Position; duration = tl.EndTime; } } catch { }
            byte[]? thumb = null;
            if (props.Thumbnail != null) thumb = await ReadThumbnailAsync(props.Thumbnail);
            var artist = props.Artist == null ? "" : string.Join(", ", props.Artist);

            return new MediaTrack(props.Title ?? "", artist, props.AlbumTitle ?? "",
                session.SourceAppUserModelId ?? "SMTC", status, position, duration, thumb);
        }
        catch { return null; }
    }

    // ==================== 通用窗口标题扫描 ====================

    /// <summary>扫描所有进程，从窗口标题中识别 "歌曲 - 歌手" 格式。</summary>
    private MediaTrack? ScanAllWindows()
    {
        // 已知的音乐播放器进程名（不含 .exe）
        var knownPlayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cloudmusic", "netease-cloud-music", "neteasecloudmusic",  // 网易云
            "qqmusic", "qqmusicplayer",                                 // QQ音乐
            "kugou", "kugoumusic", "kgmusic",                          // 酷狗
            "kwmusic", "kwm",                                           // 酷我
            "foobar2000", "musicbee", "aimp",                          // 其他
            "wmplayer", "spotify",                                      // WMP, Spotify(少)
            "thunder", "xmp",                                           // 迅雷看看等
        };

        MediaTrack? best = null;

        try
        {
            var allProcs = Process.GetProcesses();
            foreach (var p in allProcs)
            {
                try
                {
                    // 无主窗口的后台进程无需读标题——避免向大量窗口发送 WM_GETTEXT
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    // 带超时读取（150ms）：即使目标窗口消息泵卡住也不会阻塞扫描线程
                    var title = NativeMethods.GetWindowTextTimeout(p.MainWindowHandle, 150);
                    if (string.IsNullOrWhiteSpace(title) || title.Length < 2) continue;

                    // 部分播放器暂停时会在标题标注“已暂停/暂停中/[Paused]”等——识别并剥离标记
                    bool paused = false;
                    if (title.Contains("已暂停") || title.Contains("暂停中") || title.Contains("（已暂停）") || title.Contains("(已暂停)") ||
                        title.IndexOf("[Paused]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        title.IndexOf(" Paused", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        paused = true;
                        title = Regex.Replace(title,
                            "（已暂停）|\\(已暂停\\)|已暂停|暂停中|\\[Paused\\]|\\s?Paused",
                            "", RegexOptions.IgnoreCase)
                            .Trim().TrimEnd('-', '–', '—').TrimStart('-', '–', '—').Trim();
                        if (title.Length < 2) continue;
                    }

                    // 尝试从窗口标题解析 "歌曲 - 歌手" 格式
                    var (song, artist) = ParseTitle(title);
                    if (song == null) continue;

                    var procName = p.ProcessName;

                    // 检查是否是已知播放器（或标题包含明显音乐标记）
                    bool isKnown = knownPlayers.Contains(procName)
                        || title.Contains("音乐") || title.Contains("Music")
                        || title.Contains("播放") || title.Contains("Play");

                    // 过滤明显噪音（如浏览器标签页）
                    if (procName is "msedge" or "chrome" or "firefox" or "brave") continue;
                    if (song.Contains("Microsoft") || song.Contains("Windows")) continue;

                    var track = new MediaTrack(song, artist ?? "", "",
                        procName, paused ? "Paused" : "Playing", TimeSpan.Zero, TimeSpan.Zero, null);

                    if (isKnown) return track; // 已知播放器立刻返回
                    best ??= track;             // 否则保留第一个候选
                }
                catch { }
            }
        }
        catch { }

        return best;
    }

    /// <summary>从窗口标题解析 "歌曲名 - 歌手名" 格式（通用）。</summary>
    private (string? song, string? artist) ParseTitle(string title)
    {
        // 匹配 "A - B" 格式（至少 2 个字符的歌名）
        var m = Regex.Match(title, @"^(.{2,}?)\s*[-–—]\s*(.{2,}?)(?:\s*[-–—]\s*.+)?$");
        if (!m.Success) return (null, null);

        var part1 = m.Groups[1].Value.Trim();
        var part2 = m.Groups[2].Value.Trim();

        // 过滤掉非歌曲标题
        if (part1.Contains("桌面歌词") || part1.Contains("歌词") && part1.Length < 8) return (null, null);
        if (part2.Contains("桌面歌词") || part2.Contains("歌词") && part2.Length < 8) return (null, null);

        // 通常是 "歌曲 - 歌手"，但也可能是 "歌手 - 歌曲"，根据长度判断
        // 中文歌名通常较长，歌手名较短
        if (part1.Length >= part2.Length)
            return (part1, part2);
        else
            return (part2, part1); // 反转：歌手 - 歌曲 → 返回 (歌曲, 歌手)
    }

    // ==================== 封面 ====================
    private async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var buffer = new byte[stream.Size];
            reader.ReadBytes(buffer);
            return buffer;
        }
        catch { return null; }
    }
}
