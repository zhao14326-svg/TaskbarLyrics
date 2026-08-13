using TaskbarLyrics.Models;

namespace TaskbarLyrics.Services;

/// <summary>共享播放器歌词缓存（QQ/网易云/酷狗等）。</summary>
public interface IPlayerLyricsCache
{
    /// <summary>是否启用播放器缓存检索。</summary>
    bool Enabled { get; set; }

    /// <summary>缓存目录列表。</summary>
    string[] CacheFolders { get; set; }

    /// <summary>从播放器缓存目录查找歌词。</summary>
    Task<LyricsData?> TryGetLyricsAsync(MediaTrack track);

    /// <summary>重建索引（设置变更后调用）。</summary>
    void ResetIndex();

    /// <summary>后台预热索引。</summary>
    void WarmUp();
}
