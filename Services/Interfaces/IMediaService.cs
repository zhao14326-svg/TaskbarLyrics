namespace TaskbarLyrics.Services;

/// <summary>SMTC 媒体信息 + 窗口标题扫描。</summary>
public interface IMediaService
{
    /// <summary>SMTC first, window scan fallback。</summary>
    Task<MediaTrack?> GetCurrentTrackAsync();

    /// <summary>SMTC only（无回退）。</summary>
    Task<MediaTrack?> GetFromSmtcOnlyAsync();

    /// <summary>窗口标题扫描（500ms 缓存 + 后台执行，UI 线程永不阻塞）。</summary>
    MediaTrack? ScanWindowTitles();

    /// <summary>预热 SMTC 会话管理器（后台）。</summary>
    void WarmUp();

    /// <summary>SMTC 实时播放快照（同步读取，无等待）。</summary>
    SmtcMediaService.SmtcSnapshot? PollSnapshot();
}
