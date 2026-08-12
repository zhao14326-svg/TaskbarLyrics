using System.IO;
using System.Text.Json;

namespace TaskbarLyrics.Helpers;

/// <summary>
/// Caches parsed audio metadata (Artist, Title) by file path + last-write-time.
/// Avoids re-parsing ID3/FLAC tags on every app startup.
/// </summary>
public static class AudioMetaCache
{
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarLyrics");
    private static readonly string _cacheFile = Path.Combine(_dir, "audio_meta.json");
    private static Dictionary<string, CachedMeta> _index = new();
    private static bool _loaded;
    private static readonly object _lock = new();
    private static DateTime _lastSaveUtc = DateTime.MinValue;

    private record CachedMeta(string Artist, string Title, DateTime LastWrite);

    public record Info(string Artist, string Title);

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_lock)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(_cacheFile))
                {
                    var json = File.ReadAllText(_cacheFile);
                    _index = JsonSerializer.Deserialize<Dictionary<string, CachedMeta>>(json) ?? new();
                }
            }
            catch { _index = new(); }
            _loaded = true;
        }
    }

    /// <summary>Get cached metadata for an audio file. Returns null on miss.</summary>
    public static Info? TryGet(string filePath)
    {
        EnsureLoaded();
        // 直接信任缓存，不做逐文件的 GetLastWriteTime（数千首歌时该调用本身就很慢）
        if (_index.TryGetValue(filePath, out var meta))
            return new Info(meta.Artist, meta.Title);
        return null;
    }

    /// <summary>Store metadata for an audio file（批量防抖写盘，避免逐条全量序列化）。</summary>
    public static void Store(string filePath, string artist, string title)
    {
        EnsureLoaded();
        lock (_lock)
        {
            var lastWrite = File.GetLastWriteTime(filePath);
            _index[filePath] = new CachedMeta(artist, title, lastWrite);

            // 最多每 5 秒异步落盘一次，避免一次性写入几千条时阻塞
            if (DateTime.UtcNow - _lastSaveUtc > TimeSpan.FromSeconds(5))
            {
                _lastSaveUtc = DateTime.UtcNow;
                var json = JsonSerializer.Serialize(_index);
                _ = Task.Run(() =>
                {
                    try { Directory.CreateDirectory(_dir); File.WriteAllText(_cacheFile, json); }
                    catch { }
                });
            }
        }
    }
}
