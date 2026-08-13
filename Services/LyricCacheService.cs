using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TaskbarLyrics.Services;

/// <summary>
/// Two-tier lyrics cache: in-memory (ConcurrentDictionary) + persistent SQLite.
/// 7-day TTL. Stores raw LRC text keyed by "title|artist" (normalized).
/// </summary>
/// <summary>本地歌词持久缓存(内存 + SQLite)。</summary>
public interface ILyricCacheService
{
    LyricCacheService.CachedLyrics TryGet(string title, string artist);
    void Store(string title, string artist, string lrcText, string? source = null, double durationSec = 0);
}

public class LyricCacheService : ILyricCacheService
{
    private readonly ConcurrentDictionary<string, CachedLyrics> _mem = new();
    private readonly string _dbPath;
    private bool _dbReady;

    public LyricCacheService(string? dbPath = null)
    {
        if (string.IsNullOrEmpty(dbPath))
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarLyrics");
            Directory.CreateDirectory(dir);
            dbPath = Path.Combine(dir, "lyrics_cache.db");
        }
        _dbPath = dbPath;
        EnsureDb();
    }

    private void EnsureDb()
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS cache (
                    key TEXT PRIMARY KEY,
                    lyric TEXT NOT NULL,
                    source TEXT,
                    duration_sec REAL DEFAULT 0,
                    expire TEXT NOT NULL
                )
                """;
            cmd.ExecuteNonQuery();
            // 兼容旧库：无 duration_sec 列时补列
            try
            {
                cmd.CommandText = "ALTER TABLE cache ADD COLUMN duration_sec REAL DEFAULT 0";
                cmd.ExecuteNonQuery();
            }
            catch { }
            _dbReady = true;
        }
        catch { _dbReady = false; }
    }

    /// <summary>缓存条目：歌词原文 + 歌曲总时长（用于纯文本歌词估算，未知为 0）。</summary>
    public readonly record struct CachedLyrics(string? Text, double DurationSec);

    /// <summary>Try to get cached lyrics (memory → SQLite). Returns null if not found or expired.</summary>
    public CachedLyrics TryGet(string title, string artist)
    {
        var key = LyricsManager.Normalize($"{title}|{artist}");
        if (key.Length == 0) return default;

        // 1. Memory
        if (_mem.TryGetValue(key, out var cached))
            return new CachedLyrics(cached.Text, cached.DurationSec);

        // 2. SQLite
        if (!_dbReady) return default;
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT lyric, duration_sec FROM cache WHERE key=@key AND expire>@now ORDER BY expire DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var text = reader.GetString(0);
                double dur = 0;
                if (!reader.IsDBNull(1)) dur = reader.GetDouble(1);
                var entry = new CachedLyrics(text, dur);
                _mem[key] = entry;
                return entry;
            }
        }
        catch { }
        return default;
    }

    /// <summary>Store lyrics in both memory and SQLite. TTL varies by source.</summary>
    public void Store(string title, string artist, string lrcText, string? source = null, double durationSec = 0)
    {
        var key = LyricsManager.Normalize($"{title}|{artist}");
        if (key.Length == 0 || string.IsNullOrWhiteSpace(lrcText)) return;

        _mem[key] = new CachedLyrics(lrcText, durationSec);

        if (!_dbReady) return;
        try
        {
            // TTL by source: netease=1d, lrclib=3d, local=30d, default=7d
            var ttl = source switch
            {
                string s when s.Contains("网易云") => TimeSpan.FromDays(1),
                string s when s.Contains("lrclib") || s.Contains("网络歌词") => TimeSpan.FromDays(3),
                string s when s.Contains("本地") || s.Contains("LRC") || s.Contains("内嵌") => TimeSpan.FromDays(30),
                string s when s.Contains("缓存") => TimeSpan.FromDays(2),
                _ => TimeSpan.FromDays(7)
            };

            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO cache (key, lyric, source, duration_sec, expire)
                VALUES (@key, @lyric, @source, @duration_sec, @expire)
                """;
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@lyric", lrcText);
            cmd.Parameters.AddWithValue("@source", (object?)(source ?? "unknown") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@duration_sec", durationSec);
            cmd.Parameters.AddWithValue("@expire", DateTime.UtcNow.Add(ttl).ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}
