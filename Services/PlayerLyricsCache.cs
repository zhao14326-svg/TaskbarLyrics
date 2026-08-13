using System.IO;
using TaskbarLyrics.Models;

namespace TaskbarLyrics.Services;
/// <summary>
/// Shared lyrics cache with player software.
/// Scans known cache directories of QQ Music, NetEase Cloud Music, KuGou, etc.,
/// indexes .lrc files by their [ti:]/[ar:] metadata tags, and provides
/// a lookup method for matching lyrics.
/// </summary>
public class PlayerLyricsCache : IPlayerLyricsCache
{
    public bool Enabled { get; set; } = true;
    public string[] CacheFolders { get; set; } = [];

    private readonly Dictionary<string, string> _index = new(); // Normalize("title|artist") → .lrc path
    private readonly object _indexLock = new();
    private bool _indexReady;

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Default cache directories for common players (that exist at runtime).</summary>
    public static string[] DefaultCacheFolders =>
        new[]
        {
            // QQ 音乐
            Path.Combine(AppData, @"Tencent\QQMusic\QQMusicLyric"),
            Path.Combine(LocalAppData, @"Tencent\QQMusic"),
            // 网易云音乐
            Path.Combine(LocalAppData, @"Netease\CloudMusic\webdata\lyric"),
            // 酷狗
            Path.Combine(AppData, @"KuGou8\Lyric"),
            // 酷我
            Path.Combine(AppData, @"Kuwo\KuwoMusic\Lyric"),
            // 千千静听
            Path.Combine(AppData, @"TTPlayer\Lyric"),
        }.Where(Directory.Exists).ToArray();

    /// <summary>Try to find lyrics from player cache directories.</summary>
    public async Task<LyricsData?> TryGetLyricsAsync(MediaTrack track)
    {
        if (!Enabled) return null;
        if (!_indexReady) return null; // 索引后台构建中：快速跳过（WarmUp/ResetIndex 负责构建），避免阻塞首次切歌

        if (_index.Count == 0) return null;

        var key = LyricsManager.Normalize($"{track.Title}|{track.Artist}");
        var titleOnly = LyricsManager.Normalize(track.Title);

        string? path = null;
        if (!_index.TryGetValue(key, out path))
            _index.TryGetValue(titleOnly, out path);

        if (path == null) return null;

        try
        {
            var content = await File.ReadAllTextAsync(path);
            var data = LrcParser.ParseLyrics(content,
                LrcParser.EstimatePlainInterval(track.Duration.TotalSeconds, content));
            if (data.Lines.Count > 0)
            {
                var dirName = Path.GetFileName(Path.GetDirectoryName(path)) ?? "缓存";
                data.Source = $"播放器缓存({dirName})";
                return data;
            }
        }
        catch { }

        return null;
    }

    /// <summary>Rebuild the index (called after settings change).</summary>
    public void ResetIndex()
    {
        lock (_indexLock)
        {
            _indexReady = false;
            _index.Clear();
        }
        // 立即后台重建，避免下一次切歌时跳过播放器缓存检索
        _ = Task.Run(EnsureIndex);
    }

    /// <summary>后台预热歌词缓存索引（首次查找时避免阻塞）。</summary>
    public void WarmUp() => _ = Task.Run(EnsureIndex);

    private void EnsureIndex()
    {
        if (_indexReady) return;
        lock (_indexLock)
        {
            if (_indexReady) return;
            _indexReady = true;

            var folders = CacheFolders.Length > 0 ? CacheFolders : DefaultCacheFolders;
            foreach (var folder in folders)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    var files = Directory.EnumerateFiles(folder, "*.lrc", SearchOption.AllDirectories)
                        .Take(3000);
                    foreach (var file in files)
                    {
                        try
                        {
                            IndexFile(file);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
    }

    private void IndexFile(string filePath)
    {
        // Read first ~1KB to scan for [ti:]/[ar:] metadata
        var buffer = new byte[1024];
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int read = fs.Read(buffer, 0, buffer.Length);
        if (read <= 0) return;

        // Try UTF-8 first, fallback to Latin1-like
        string text;
        try { text = System.Text.Encoding.UTF8.GetString(buffer, 0, read); }
        catch { return; }

        var (title, artist) = LrcParser.GetMetadata(text);

        // Also try filename-based matching as fallback
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var fileNameNorm = LyricsManager.Normalize(fileName);

        if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist))
        {
            var key = LyricsManager.Normalize($"{title}|{artist}");
            if (key.Length > 1)
                _index.TryAdd(key, filePath);
        }

        // Filename fallback: if the filename has meaningful text, index it too
        if (fileNameNorm.Length > 2)
        {
            _index.TryAdd(fileNameNorm, filePath);
        }
    }
}
