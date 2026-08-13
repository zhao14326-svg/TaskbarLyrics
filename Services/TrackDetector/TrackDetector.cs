using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace TaskbarLyrics.Services;

/// <summary>
/// 曲目检测编排：窗口标题 → SMTC → 本地 API 多路兜底。
/// 窗口标题（&lt;1ms 快速检测）为主源；播放器关闭窗口（后台播放）时，
/// 依次用 SMTC 会话、网易云本地 API 兜底；全部失效且播放器进程仍存活时保持当前曲目，
/// 保证歌词/封面持续显示。带播放位置校准（拖进度条/seek/暂停恢复即时同步）。
/// </summary>
public class TrackDetector : ITrackDetector
{
    private readonly WindowTitleParser _titleParser;
    private readonly SmtcResolver _smtc;
    private readonly LocalApiResolver _localApi;
    private readonly TrackNormalizer _normalizer;
    private readonly ILogger<TrackDetector> _logger;

    // 常见播放器进程名（用于窗口关闭后台播放时的进程存活检测）
    private static readonly string[] KnownPlayerProcesses =
    [
        "cloudmusic", "netease-cloud-music", "qqmusic", "qqmusicplayer",
        "kugou", "kugoumusic", "kgmusic", "kwmusic", "kwm",
        "foobar2000", "musicbee", "wmplayer", "spotify",
    ];

    private MediaTrack? _currentTrack;
    private DateTime _trackStartTime;
    private DateTime _lastDetectedAt = DateTime.MinValue; // 最后成功解析到曲目的时间（检测失败宽限基准）
    private double _lastKnownPos;
    private bool _isPaused;
    private TimeSpan _duration;
    private string _lastId = "";

    public TrackDetector(WindowTitleParser titleParser, SmtcResolver smtc, LocalApiResolver localApi,
        TrackNormalizer normalizer, ILogger<TrackDetector> logger)
    {
        _titleParser = titleParser;
        _smtc = smtc;
        _localApi = localApi;
        _normalizer = normalizer;
        _logger = logger;
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
        var windowTrack = _titleParser.ScanWindowTitles();

        if (windowTrack != null)
        {
            var id = _normalizer.NormalizeTrack(windowTrack.Title, windowTrack.Artist);
            if (id.Length == 0) goto hold;

            _lastDetectedAt = DateTime.UtcNow; // 成功解析到曲目，记录检测时间

            if (id != _lastId) // New song — reset clock
            {
                // 窗口标题的细微变化（歌手-歌名顺序颠倒、(Live) 后缀等）不应触发切歌：
                // 与当前曲目去版本后缀匹配时视为同一首，保持原曲目与进度时钟
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
    /// 完整兜底检测（异步）：窗口标题 → SMTC 会话 → 本地播放器 API。
    /// 播放器关闭窗口（后台播放）时窗口标题不可读，但 SMTC 会话或本地 API 仍提供曲目，
    /// 依次尝试，成功后接管检测器状态（位置时钟/暂停/时长），使歌词与封面持续显示。
    /// </summary>
    public async Task<MediaTrack?> DetectWithFallbackAsync()
    {
        var track = Detect();
        if (track != null) return track;

        // SMTC 兜底（全局多会话）
        try
        {
            var smtc = await _smtc.GetFromSmtcOnlyAsync();
            if (smtc is { Title.Length: > 0 })
            {
                _logger.LogDebug("曲目来源=SMTC: {Title} ({App})", smtc.Title, smtc.PlaybackApp);
                AdoptTrack(smtc);
                return smtc;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SMTC 兜底异常"); }

        // 本地播放器 API 兜底（网易云；SMTC 不可用环境的关闭窗口场景）
        try
        {
            var local = await _localApi.GetTrackAsync();
            if (local is { Title.Length: > 0 })
            {
                _logger.LogDebug("曲目来源=本地API: {Title}", local.Title);
                AdoptTrack(local);
                return local;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "本地 API 兜底异常"); }

        // 所有来源暂时失效：若播放器进程仍存活（关闭窗口后台播放）或距上次确认播放未超 30s，
        // 保持当前曲目，使歌词/封面持续显示；播放器已退出才清空
        if (_currentTrack != null &&
            ((DateTime.UtcNow - _lastDetectedAt).TotalSeconds < 30 || IsPlayerAlive()))
        {
            _logger.LogTrace("所有来源失效，保持当前曲目: {Title}", _currentTrack.Title);
            return _currentTrack;
        }

        _logger.LogTrace("播放器已退出或无当前曲目，清空检测");
        _lastId = "";
        return null;
    }

    /// <summary>检测上次曲目来源的播放器进程是否仍存活（关闭窗口后台播放时用于决定是否保持当前曲目）。</summary>
    private bool IsPlayerAlive()
    {
        if (_currentTrack == null) return false;
        var app = _currentTrack.PlaybackApp;
        if (!string.IsNullOrEmpty(app) &&
            !app.Contains("SMTC", StringComparison.OrdinalIgnoreCase) &&
            !app.Contains("Local", StringComparison.OrdinalIgnoreCase) &&
            !app.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            try { if (Process.GetProcessesByName(app).Length > 0) return true; } catch { }
        }
        // 兜底：检查常见播放器进程（本地 API 来源的 PlaybackApp="NeteaseLocal" 等场景）
        foreach (var p in KnownPlayerProcesses)
        {
            try { if (Process.GetProcessesByName(p).Length > 0) return true; } catch { }
        }
        return false;
    }

    /// <summary>
    /// 播放器关闭窗口（后台播放）或窗口标题不可读时，用兜底来源（SMTC/本地API）的
    /// 曲目接管检测器状态，使 GetPosition/歌词校准继续工作。
    /// </summary>
    public void AdoptTrack(MediaTrack track)
    {
        if (track == null || string.IsNullOrEmpty(track.Title)) return;
        _currentTrack = track;
        _lastId = _normalizer.NormalizeTrack(track.Title, track.Artist);
        _trackStartTime = DateTime.UtcNow - track.Position;
        _lastKnownPos = track.Position.TotalSeconds;
        _lastDetectedAt = DateTime.UtcNow;   // 重置窗口检测 hold 计时
        _duration = track.Duration;
        _isPaused = !track.PlaybackStatus.Equals("Playing", StringComparison.OrdinalIgnoreCase);
        _hasCalib = false;
    }

    /// <summary>曲目是否同一首（去版本后缀后的曲目名+歌手比对，容忍"歌手-歌名"顺序颠倒）。</summary>
    private bool SameSong(MediaTrack a, MediaTrack b)
    {
        if (a == null || b == null) return false;
        var aKey = _normalizer.NormalizeTrack(a.Title, a.Artist);
        var bKey = _normalizer.NormalizeTrack(b.Title, b.Artist);
        if (aKey.Length == 0 || bKey.Length == 0) return false;
        if (aKey == bKey) return true;

        // 兼容“歌手-歌名”被窗口标题颠倒解析：歌名/歌手交叉匹配
        var aT = _normalizer.NormalizeTitle(a.Title);
        var bT = _normalizer.NormalizeTitle(b.Title);
        var aA = LyricsManager.Normalize(a.Artist);
        var bA = LyricsManager.Normalize(b.Artist);
        if (aT.Length > 0 && aT == bA && aA == bT) return true;
        return false;
    }

    /// <summary>
    /// 归零：重置检测与校准状态（应用启动/重新显示时调用），
    /// 使下次检测从干净的零点开始，再由 SMTC 自动校准到真实进度。
    /// </summary>
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

