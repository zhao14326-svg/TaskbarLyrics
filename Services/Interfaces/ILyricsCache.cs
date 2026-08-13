namespace TaskbarLyrics.Services;

/// <summary>本地歌词持久缓存条目。</summary>
public readonly record struct CachedLyrics(string? Text, double DurationSec);

/// <summary>本地歌词持久缓存（内存 + SQLite，7 天 TTL）。</summary>
public interface ILyricsCache
{
    /// <summary>读取缓存（memory → SQLite）。返回 default 表示未命中或已过期。</summary>
    CachedLyrics TryGet(string title, string artist);

    /// <summary>写入缓存（memory + SQLite），TTL 按来源区分。</summary>
    void Store(string title, string artist, string lrcText, string? source = null, double durationSec = 0);
}
