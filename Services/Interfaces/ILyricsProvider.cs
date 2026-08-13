using TaskbarLyrics.Models;

namespace TaskbarLyrics.Services;

/// <summary>统一歌词获取接口（本地/缓存/在线全链路）。</summary>
public interface ILyricsProvider
{
    /// <summary>音乐目录（用于查找本地内嵌歌词/封面）。</summary>
    string[] MusicFolders { get; set; }

    /// <summary>是否启用在线歌词获取。</summary>
    bool EnableOnline { get; set; }

    /// <summary>播放器缓存共享。</summary>
    IPlayerLyricsCache PlayerCache { get; set; }

    /// <summary>已索引的音频文件列表（供封面复用）。</summary>
    IReadOnlyList<string> AudioFiles { get; }

    /// <summary>当前是否显示波形动画（无歌词时）。</summary>
    bool IsInstrumental { get; set; }

    /// <summary>当前歌词。</summary>
    LyricsData? Current { get; set; }

    /// <summary>根据曲目信息获取歌词（带缓存 + 两阶段渐进加载）。</summary>
    Task<LyricsData?> GetLyricsAsync(MediaTrack track);

    /// <summary>当前句歌词文本。</summary>
    string? GetCurrentLine(TimeSpan position);

    /// <summary>下一句歌词文本。</summary>
    string? GetNextLine(TimeSpan position);

    /// <summary>当前句播放进度（0-1）。</summary>
    double GetLineProgress(TimeSpan position);

    /// <summary>重建音频索引（设置变更后调用）。</summary>
    void ResetIndex();

    /// <summary>后台预热索引。</summary>
    void WarmUp();
}
