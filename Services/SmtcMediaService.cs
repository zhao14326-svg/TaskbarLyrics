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

    // ==================== SMTC（全局多会话） ====================

    /// <summary>
    /// 遍历所有媒体会话读取当前曲目（全局 SMTC）。
    /// 网易云等播放器若不是系统"当前会话"（如浏览器占用当前会话）时，
    /// GetCurrentSession() 读不到，需遍历 GetSessions() 按优先级选取：
    /// ① 活跃会话(position>0 或 Playing) ② 已知音乐播放器会话 ③ 首个有标题的会话。
    /// </summary>
    private async Task<MediaTrack?> GetFromSmtcAsync()
    {
        try
        {
            var manager = await GetManagerAsync();
            if (manager == null) return null;

            // 会话选择优先级：活跃 > 已知音乐播放器 > 首个有效标题
            GlobalSystemMediaTransportControlsSession? active = null;
            GlobalSystemMediaTransportControlsSession? knownPlayer = null;
            GlobalSystemMediaTransportControlsSession? firstValid = null;
            try
            {
                foreach (var s in manager.GetSessions())
                {
                    try
                    {
                        var props = await s.TryGetMediaPropertiesAsync();
                        if (string.IsNullOrEmpty(props?.Title)) continue;
                        var st = s.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "";
                        var tl = s.GetTimelineProperties();
                        var isActive = tl != null &&
                            (tl.Position > TimeSpan.Zero || st.Equals("Playing", StringComparison.OrdinalIgnoreCase));
                        var appId = s.SourceAppUserModelId ?? "";
                        if (isActive) { active = s; break; }        // 活跃会话立即使用
                        if (knownPlayer == null && IsKnownPlayerApp(appId)) knownPlayer = s;
                        firstValid ??= s;
                    }
                    catch { }
                }
            }
            catch { }

            var session = active ?? knownPlayer ?? firstValid ?? manager.GetCurrentSession();
            if (session == null) return null;
            var mediaProps = await session.TryGetMediaPropertiesAsync();
            if (string.IsNullOrEmpty(mediaProps?.Title)) return null;

            var status = session.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "Unknown";
            var position = TimeSpan.Zero;
            var duration = TimeSpan.Zero;
            try { var tl = session.GetTimelineProperties(); if (tl != null) { position = tl.Position; duration = tl.EndTime; } } catch { }
            byte[]? thumb = null;
            if (mediaProps.Thumbnail != null) thumb = await ReadThumbnailAsync(mediaProps.Thumbnail);
            var artist = mediaProps.Artist == null ? "" : string.Join(", ", mediaProps.Artist);

            return new MediaTrack(mediaProps.Title ?? "", artist, mediaProps.AlbumTitle ?? "",
                session.SourceAppUserModelId ?? "SMTC", status, position, duration, thumb);
        }
        catch { return null; }
    }

    /// <summary>判断 SMTC 会话是否来自常见音乐播放器（用于全局会话优先级选择）。</summary>
    private static bool IsKnownPlayerApp(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        var id = appId.ToLowerInvariant();
        return id.Contains("cloudmusic") || id.Contains("netease") || id.Contains("qqmusic")
            || id.Contains("kugou") || id.Contains("kuwo") || id.Contains("kwmusic")
            || id.Contains("spotify") || id.Contains("foobar") || id.Contains("musicbee")
            || id.Contains("wmplayer") || id.Contains("mediaplayer") || id.Contains("aimp")
            || id.Contains("itunes") || id.Contains("applemusic") || id.Contains("migu");
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
