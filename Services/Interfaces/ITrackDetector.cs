using TaskbarLyrics.Services;

namespace TaskbarLyrics.Services;

/// <summary>曲目检测（窗口标题扫描 + 播放位置校准）。</summary>
public interface ITrackDetector
{
    /// <summary>快速检测当前曲目（窗口标题）。</summary>
    MediaTrack? Detect();

    /// <summary>完整兜底检测（异步）：窗口标题 → SMTC → 本地播放器 API。</summary>
    Task<MediaTrack?> DetectWithFallbackAsync();

    /// <summary>归零：重置检测与校准状态。</summary>
    void Reset();

    /// <summary>设置暂停状态。</summary>
    void SetPaused(bool paused);

    /// <summary>用真实播放进度建立本地时钟基线。</summary>
    void CalibrateStart(TimeSpan position, TimeSpan duration);

    /// <summary>无条件对齐到给定位置（seek/拖进度条）。</summary>
    void AlignTo(TimeSpan position);

    /// <summary>校准：播放中信任本地时钟，仅对跳变响应。</summary>
    void Calibrate(TimeSpan position, bool isPlaying);

    /// <summary>当前播放位置。</summary>
    TimeSpan GetPosition();

    /// <summary>播放器关闭窗口（后台播放）时，用兜底来源（SMTC/本地API）的曲目接管检测器状态。</summary>
    void AdoptTrack(MediaTrack track);

    /// <summary>当前曲目总时长（来自 SMTC）。</summary>
    TimeSpan Duration { get; }

    bool IsNewSong { get; }
    string CurrentId { get; }
    MediaTrack? Current { get; }
}
