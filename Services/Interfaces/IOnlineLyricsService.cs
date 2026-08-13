using TaskbarLyrics.Models;

namespace TaskbarLyrics.Services;

/// <summary>在线歌词获取。</summary>
public interface IOnlineLyricsService
{
    /// <summary>并行尝试 lrclib 精确 / 网易云 / lrclib 宽搜，返回首个有效歌词。</summary>
    Task<LyricsData?> FetchAsync(string title, string artist, string? album = null, double durationSec = 0);
}
