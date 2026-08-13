using System.Net.Http;
using System.Text.Json;

namespace TaskbarLyrics.Services;
/// <summary>
/// Fast/slow separated track detector.
/// Window scan (<1ms) for instant detection, SMTC for position calibration.
/// Holds current track for 5s when detection temporarily fails.
/// </summary>
public class TrackDetector : ITrackDetector
{
    private readonly IMediaService _smtc;
    private MediaTrack? _currentTrack;
    private DateTime _trackStartTime;
    private DateTime _lastDetectedAt = DateTime.MinValue; // 最后成功解析到曲目的时间（检测失败宽限基准）
    private double _lastKnownPos;
    private bool _isPaused;
    private TimeSpan _duration;
    private string _lastId = "";

    public TrackDetector(IMediaService smtc)
    {
        _smtc = smtc;
    }

    // 校准状态
    private TimeSpan _lastCalibPos;
    private DateTime _lastCalibTime;
    private bool _hasCalib;

    // 首次校准允许的最大基线误差（秒）：超过则对齐真实进度
    private const double BaseCalibrationThresholdSec = 0.5;
    // 播放中仅对“跳变”（拖进度条/循环/切歌）响应，避免跟随上报延迟
    private const double JumpCalibrationThresholdSec = 2.0;

    /// <summary>Detect track fast. Window scan first, skip slow sources.</summary>
    public MediaTrack? Detect()
    {
        var windowTrack = _smtc.ScanWindowTitles();

        if (windowTrack != null)
        {
            var id = LyricsManager.Normalize($"{windowTrack.Title}|{windowTrack.Artist}");
            if (id.Length == 0) goto hold;

            _lastDetectedAt = DateTime.UtcNow; // 成功解析到曲目，记录检测时间

            if (id != _lastId) // New song — reset clock
            {
                // 窗口标题的细微变化（歌手-歌名顺序颠倒等）不应触发切歌：
                // 与当前曲目模糊匹配时视为同一首，保持原曲目与进度时钟，避免反复重新获取歌词/封面
                if (_currentTrack != null && SameSong(_currentTrack, windowTrack))
                {
                    windowTrack = _currentTrack;
                    id = _lastId;
                }
                else
                {
                    _currentTrack = windowTrack;
                    _lastId = id;
                    _trackStartTime = DateTime.UtcNow;
                    _lastKnownPos = 0;
                    _duration = TimeSpan.Zero;
                    _isPaused = false;
                    _hasCalib = false;
                }
            }
            return _currentTrack;
        }

    hold:
        // 检测短暂失败（网易云最小化、窗口标题短暂不可读、播放器过渡）时保持上一首，
        // 宽限 8 秒，避免把“检测暂时失败”误判为“停止播放”导致悬浮窗按 AutoHide 隐藏或状态抖动
        if (_currentTrack != null && (DateTime.UtcNow - _lastDetectedAt).TotalSeconds < 8)
            return _currentTrack;

        _lastId = "";
        return null;
    }

    /// <summary>
    /// 归零：重置检测与校准状态（应用启动/重新显示时调用），
    /// 使下次检测从干净的零点开始，再由 SMTC 自动校准到真实进度。
    /// </summary>
    /// <summary>
    /// 播放器关闭窗口(后台播放)时窗口标题不可读:用 SMTC 会话曲目接管检测器状态,
    /// 使 GetPosition/歌词校准继续工作(由 RefreshAsync 在窗口扫描失败时调用)。
    /// </summary>
    public void AdoptSmtcTrack(MediaTrack track)
    {
        if (track == null || string.IsNullOrEmpty(track.Title)) return;
        _currentTrack = track;
        _lastId = LyricsManager.Normalize($"{track.Title}|{track.Artist}");
        _trackStartTime = DateTime.UtcNow - track.Position;
        _lastKnownPos = track.Position.TotalSeconds;
        _lastDetectedAt = DateTime.UtcNow;   // 重置窗口检测 hold 计时
        _duration = track.Duration;
        _isPaused = !track.PlaybackStatus.Equals("Playing", StringComparison.OrdinalIgnoreCase);
        _hasCalib = false;
    }

    public void Reset()
    {
        _currentTrack = null;
        _lastId = "";
        _trackStartTime = DateTime.UtcNow;
        _lastDetectedAt = DateTime.MinValue;
        _lastKnownPos = 0;
        _duration = TimeSpan.Zero;
        _isPaused = false;
        _lastCalibPos = TimeSpan.Zero;
        _lastCalibTime = DateTime.MinValue;
        _hasCalib = false;
    }

    /// <summary>模糊判断两首曲目是否为同一首歌（歌名一致，或“歌手-歌名”与“歌名-歌手”顺序颠倒）。</summary>
    private static bool SameSong(MediaTrack a, MediaTrack b)
    {
        var aT = LyricsManager.Normalize(a.Title);
        var bT = LyricsManager.Normalize(b.Title);
        var aA = LyricsManager.Normalize(a.Artist);
        var bA = LyricsManager.Normalize(b.Artist);
        if (aT.Length == 0 || bT.Length == 0) return false;

        // 歌名一致（歌手一侧为空时视为同一首）
        if (aT == bT && (aA.Length == 0 || bA.Length == 0 || aA == bA)) return true;
        // “歌手-歌名”与“歌名-歌手”顺序颠倒
        if (aA.Length > 0 && aT == bA && aA == bT) return true;
        return false;
    }

    /// <summary>当前播放位置：本地时钟（已校准时与真实播放进度一致）。</summary>
    public TimeSpan GetPosition()
    {
        if (_currentTrack == null) return TimeSpan.Zero;
        if (_isPaused) return TimeSpan.FromSeconds(_lastKnownPos);
        return TimeSpan.FromSeconds(Math.Max(0, (DateTime.UtcNow - _trackStartTime).TotalSeconds));
    }

    public void SetPaused(bool paused)
    {
        if (paused && !_isPaused) _lastKnownPos = GetPosition().TotalSeconds;
        else if (!paused && _isPaused) _trackStartTime = DateTime.UtcNow - TimeSpan.FromSeconds(_lastKnownPos);
        _isPaused = paused;
    }

    /// <summary>
    /// 换歌时用真实播放进度建立本地时钟基线（SMTC 会话已验证与当前曲目一致后调用）。
    /// </summary>
    public void CalibrateStart(TimeSpan position, TimeSpan duration)
    {
        _trackStartTime = DateTime.UtcNow - position;
        _lastKnownPos = position.TotalSeconds;
        if (duration.TotalMilliseconds > 0)
            _duration = duration;
        _isPaused = false;
        _lastCalibPos = position;
        _lastCalibTime = DateTime.UtcNow;
        _hasCalib = false;
    }

    /// <summary>
    /// 用户拖动进度条/seek：无条件将本地时钟对齐到给定位置（歌词立即跟随进度条回滚/快进）。
    /// 与 Calibrate 不同：不判断跳变幅度、不依赖会话验证状态，直接对齐，
    /// 用于响应明确的 seek 意图（进度条前后拖动、循环、切歌后位置跳变）。
    /// </summary>
    public void AlignTo(TimeSpan position)
    {
        if (position.TotalMilliseconds < 0) return;
        _trackStartTime = DateTime.UtcNow - position;
        _lastKnownPos = position.TotalSeconds;
        _lastCalibPos = position;
        _lastCalibTime = DateTime.UtcNow;
        _hasCalib = true;
        _isPaused = false;
    }

    /// <summary>
    /// 校准：切歌时用真实进度建基线；播放中信任本地 1x 真实时钟（无上报延迟），
    /// 仅对“跳变”（拖进度条/循环/切歌/暂停恢复）响应重对齐。
    /// </summary>
    public void Calibrate(TimeSpan position, bool isPlaying)
    {
        if (position.TotalMilliseconds < 0) return;
        var now = DateTime.UtcNow;

        if (!isPlaying)
        {
            _lastKnownPos = position.TotalSeconds;
            _isPaused = true;
            return;
        }

        var localPos = (now - _trackStartTime).TotalSeconds;

        if (_isPaused)
        {
            // 暂停恢复：本地时钟早已超前，强制对齐真实进度
            _trackStartTime = now - position;
        }
        else if (position.TotalSeconds == 0 && _hasCalib && _lastCalibPos.TotalSeconds > 0 && localPos > 3)
        {
            // 位置从非零归零 → 发生了循环/切歌，重对齐到 0
            _trackStartTime = now - position;
        }
        else if (!_hasCalib)
        {
            // 首次校准：基线误差超过 0.5s 即对齐（消除检测滞后）
            if (Math.Abs(position.TotalSeconds - localPos) > BaseCalibrationThresholdSec)
                _trackStartTime = now - position;
        }
        else
        {
            // 播放中信任本地 1x 时钟（真实时间，避免跟随 SMTC 上报延迟）；
            // 仅当位置增量明显偏离真实流逝时间（seek/循环/切歌）时重对齐
            var elapsed = (now - _lastCalibTime).TotalSeconds;
            var delta = (position - _lastCalibPos).TotalSeconds;
            if (elapsed > 0.05 && Math.Abs(delta - elapsed) > JumpCalibrationThresholdSec)
                _trackStartTime = now - position;
        }

        _lastCalibPos = position;
        _lastCalibTime = now;
        _hasCalib = true;
        _lastKnownPos = position.TotalSeconds;
        _isPaused = false;
    }

    /// <summary>当前曲目总时长（来自 SMTC，未获取时为 0）。</summary>
    public TimeSpan Duration => _duration;

    public bool IsNewSong => _lastId != "";
    public string CurrentId => _lastId;
    public MediaTrack? Current => _currentTrack;
}
