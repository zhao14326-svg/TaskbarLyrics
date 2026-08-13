using Windows.Media.Control;
using Windows.Storage.Streams;
using TaskbarLyrics.Helpers;

namespace TaskbarLyrics.Services;

public record MediaTrack(
    string Title, string Artist, string Album,
    string PlaybackApp, string PlaybackStatus,
    TimeSpan Position, TimeSpan Duration, byte[]? ThumbnailBytes);

/// <summary>
/// SMTC 媒体会话读取（播放信息 / 缩略图 / 实时快照）。
/// 窗口标题扫描已拆分到 WindowTitleParser。
/// </summary>
public class SmtcMediaService : IMediaService
{
    private readonly WindowTitleParser _titleParser;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _smtcFailed;

    public SmtcMediaService(WindowTitleParser titleParser)
    {
        _titleParser = titleParser;
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync()
    {
        if (_smtcFailed) return null;
        if (_manager != null) return _manager;
        try { _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch { _smtcFailed = true; }
        return _manager;
    }

    /// <summary>SMTC first, window scan fallback.</summary>
    public async Task<MediaTrack?> GetCurrentTrackAsync()
    {
        var track = await GetFromSmtcAsync();
        return track ?? _titleParser.ScanWindowTitles();
    }

    /// <summary>SMTC only (no fallback).</summary>
    public async Task<MediaTrack?> GetFromSmtcOnlyAsync() => await GetFromSmtcAsync();

    /// <summary>窗口标题扫描（委托 WindowTitleParser）。</summary>
    public MediaTrack? ScanWindowTitles() => _titleParser.ScanWindowTitles();

    /// <summary>预热 SMTC 会话管理器（后台），使 PollSnapshot 能立即读到真实播放进度。</summary>
    public void WarmUp() { _ = GetManagerAsync(); }

    /// <summary>SMTC 实时播放快照（同步读取，无等待，用于每帧校准）。</summary>
    public sealed record SmtcSnapshot(string AppId, TimeSpan Position, TimeSpan EndTime, string Status);

    /// <summary>
    /// 同步读取媒体会话的实时播放状态。多实例播放器（多个 SMTC 会话）时，
    /// 遍历 GetSessions() 优先选“活跃”会话（position>0 或 Playing），
    /// 避免 GetCurrentSession() 返回陈旧/空会话导致校准失效。无活跃会话时回退当前会话。
    /// </summary>
    public SmtcSnapshot? PollSnapshot()
    {
        if (_smtcFailed || _manager == null) return null;
        try
        {
            GlobalSystemMediaTransportControlsSession? session = null;
            try
            {
                foreach (var s in _manager.GetSessions())
                {
                    try
                    {
                        var t = s.GetTimelineProperties();
                        var st = s.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "";
                        if (t != null && (t.Position > TimeSpan.Zero ||
                            st.Equals("Playing", StringComparison.OrdinalIgnoreCase)))
                        {
                            session = s;
                            break;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            session ??= _manager.GetCurrentSession();
            if (session == null) return null;

            var tl = session.GetTimelineProperties();
            if (tl == null) return null;
            var status = session.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "";
            var appId = session.SourceAppUserModelId ?? "SMTC";
            return new SmtcSnapshot(appId, tl.Position, tl.EndTime, status);
        }
        catch { return null; }
    }

    // ==================== SMTC ====================

    private async Task<MediaTrack?> GetFromSmtcAsync()
    {
        try
        {
            var manager = await GetManagerAsync();
            if (manager == null) return null;
            var session = manager.GetCurrentSession();
            if (session == null) return null;
            var props = await session.TryGetMediaPropertiesAsync();
            if (string.IsNullOrEmpty(props?.Title)) return null;

            var status = session.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "Unknown";
            var position = TimeSpan.Zero;
            var duration = TimeSpan.Zero;
            try { var tl = session.GetTimelineProperties(); if (tl != null) { position = tl.Position; duration = tl.EndTime; } } catch { }
            byte[]? thumb = null;
            if (props.Thumbnail != null) thumb = await ReadThumbnailAsync(props.Thumbnail);
            var artist = props.Artist == null ? "" : string.Join(", ", props.Artist);

            return new MediaTrack(props.Title ?? "", artist, props.AlbumTitle ?? "",
                session.SourceAppUserModelId ?? "SMTC", status, position, duration, thumb);
        }
        catch { return null; }
    }

    private async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            var buffer = new byte[stream.Size];
            reader.ReadBytes(buffer);
            return buffer;
        }
        catch { return null; }
    }
}
