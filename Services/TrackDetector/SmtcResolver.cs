namespace TaskbarLyrics.Services;

/// <summary>
/// SMTC 读取封装：为 TrackDetector 提供 SMTC 会话曲目 / 实时快照。
/// 依赖 IMediaService（SMTC 能力），供曲目检测多路兜底编排使用。
/// </summary>
public class SmtcResolver
{
    private readonly IMediaService _media;

    public SmtcResolver(IMediaService media)
    {
        _media = media;
    }

    /// <summary>SMTC 会话曲目（无窗口标题回退）。</summary>
    public Task<MediaTrack?> GetFromSmtcOnlyAsync() => _media.GetFromSmtcOnlyAsync();

    /// <summary>SMTC 实时播放快照（同步读取，用于位置校准）。</summary>
    public SmtcMediaService.SmtcSnapshot? PollSnapshot() => _media.PollSnapshot();

    /// <summary>预热 SMTC 会话管理器（后台）。</summary>
    public void WarmUp() => _media.WarmUp();
}
