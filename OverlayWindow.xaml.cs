using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TaskbarLyrics.Helpers;
using TaskbarLyrics.Models;
using TaskbarLyrics.Services;

namespace TaskbarLyrics;

public partial class OverlayWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ILyricsProvider _lyrics;
    private readonly IMediaService _smtc;
    private readonly ICoverArtProvider _coverArt;
    private readonly IPlayerLocalApiService _localApi;
    private readonly Random _rng = new();
    // 后台线程定时器替代 DispatcherTimer：DispatcherTimer 依赖低优先级 WM_TIMER，
    // 会被 WebView2 的 WM_PAINT 消息洪泛饿死（tick 从 250ms 恶化到数秒，导致歌词冻结不滚动）。
    private System.Threading.Timer? _tickTimer;
    private int _refreshQueued;
    private bool _webReady;
    private string _lastKey = "";
    private string _lastCoverKey = "";
    private double _dpiScale = 1.0;
    private IntPtr _windowHwnd;
    private IntPtr _oldRgn;
    private const int CornerRadius = 8;

    // Player-linked state
    private bool _wasPlaying;
    private DateTime _stoppedAt;
    // 用户手动隐藏标志（托盘/设置按钮）：意外隐藏恢复逻辑不干扰手动隐藏
    private bool _userHidden;

    private readonly ITrackDetector _detector;

    // Spectrum simulation
    private float[] _specBands = new float[24];
    private float[] _specTargets = new float[24];
    private long _specFrame;
    private long _specLastSentFrame;

    // Spectrum active state (resolved by mode logic in RefreshAsync)
    private bool _specShouldSend;

    // Delta-based state pushing: only send when values actually change
    private int _lastSentIndex = -2;
    private string _lastSentMode = "";
    private bool _lastSentPlaying;
    private bool _lastSentSpec;
    // SMTC 校准状态
    private string? _smtcApp;        // 已验证与当前曲目一致的 SMTC 应用标识（同一播放器内切歌保持信任）
    private string? _verifiedApp;    // 最近一次验证过的应用标识（即使不匹配也记录，避免反复验证同一应用）
    private Task<MediaTrack?>? _pendingSmtc;

    // 新歌歌词获取中（计数：快速连续切歌时多个获取任务重叠，期间前端保持 loading）
    private int _fetchingLyrics;
    // 最近推送的歌词标识（trackKey|source）：后台在线歌词/升级结果到达时自动重新推送
    private string _lastSentLyricsMarker = "";
    // 封面请求序号：快速切歌时丢弃旧歌的封面结果，避免旧封面覆盖新封面
    private int _coverReqSeq;

    // 播放状态判定辅助（兼容切歌后 SMTC 状态上报滞后的播放器）
    private DateTime _newTrackAt = DateTime.MinValue;   // 最近一次切歌时间（切歌宽限期）
    private DateTime _lastSmtcAdvance = DateTime.MinValue; // 最近一次观察到 SMTC 位置前进
    private TimeSpan? _prevSmtcPos;                     // 上一次 SMTC 位置
    private DateTime _lastSnapAt = DateTime.MinValue;    // 上一次 SMTC 快照时刻（用于 seek 跳变检测）

    // 无可信 SMTC 时的本地播放器 API 状态校验（网易云暂停时窗口标题不标注暂停，需用本地 API 确认）
    private bool? _localApiPlaying;                     // 本地 API 最近一次播放状态（null=不可用）
    private DateTime _localApiCheckedAt = DateTime.MinValue;
    // 频谱暂停淡出截止时间（关闭后保留短暂渲染窗口，让前端平滑衰减再隐藏）
    private DateTime _specFadeEnd;

    // Auto-size
    private double _reportedContentW, _reportedContentH;

    public OverlayWindow(AppSettings settings, ILyricsProvider lyrics,
        IMediaService smtc, ICoverArtProvider coverArt, IPlayerLocalApiService localApi, ITrackDetector detector)
    {
        InitializeComponent();
        _settings = settings;
        _lyrics = lyrics;
        _smtc = smtc;
        _coverArt = coverArt;
        _localApi = localApi;
        _detector = detector;
        Topmost = _settings.AlwaysOnTop;
        CompositionTarget.Rendering += OnRendering;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>后台定时器触发：通过 Dispatcher.BeginInvoke 回到 UI 线程执行刷新（防重入）。</summary>
    private void QueueRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshQueued, 1, 0) != 0) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await RefreshAsync(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Refresh: " + ex.Message); }
                finally { Interlocked.Exchange(ref _refreshQueued, 0); }
            }));
        }
        catch { Interlocked.Exchange(ref _refreshQueued, 0); }
    }

    // ==================== Init ====================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var src = PresentationSource.FromVisual(this);
        _dpiScale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        _windowHwnd = new WindowInteropHelper(this).Handle;
        PositionWindow();
        ApplyRoundedRegion();
        // 注释:系统级亚克力在分层窗口(AllowsTransparency)上通常无效,且可能增加 DWM 开销;
        // 外观由 CSS 半透明渐变承担,不再调用 EnableAcrylic 以免干扰系统合成
        // 悬浮窗不 attach 到任务栏：attach（owner 关系）会导致任务栏隐藏/最小化时悬浮窗跟随消失。
        // 每 tick 由 EnsureTopmost 强制置顶，保证点击任务栏时悬浮窗仍盖在任务栏之上。
        NativeMethods.MakeClickThrough(_windowHwnd);
        await InitWebViewAsync();

        // 启动即“归零 + 预热”：重置校准状态到零点，并立即预热 SMTC，
        // 使首个刷新帧就能读到真实播放进度完成自动校准
        _detector.Reset();
        _smtc.WarmUp();

        if (_settings.PlayerAutoHide)
        {
            Hide();
        }
        // 启动 250ms 心跳（后台线程触发，BeginInvoke 回 UI 线程执行）
        _tickTimer?.Dispose();
        _tickTimer = new System.Threading.Timer(_ => QueueRefresh(), null, 250, 250);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _tickTimer?.Dispose();
        _tickTimer = null;
        if (_oldRgn != IntPtr.Zero) { NativeMethods.DeleteObject(_oldRgn); _oldRgn = IntPtr.Zero; }
    }

    /// <summary>初始化 WebView2：读取内嵌资源并组装完整 HTML，内联 CSS/JS。</summary>
    private async Task InitWebViewAsync()
    {
        try
        {
            // Use pre-warmed environment if available, otherwise create on-demand
            var env = App.WebView2Env;
            if (env != null)
                await Web.EnsureCoreWebView2Async(env);
            else
                await Web.EnsureCoreWebView2Async();

            var cwv2 = Web.CoreWebView2;
            cwv2.Settings.AreDefaultContextMenusEnabled = false;
            cwv2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // 注册虚拟主机用于加载字体（字体太大不适合 base64 内联）
            AppAssetServer.Register(cwv2);

            cwv2.WebMessageReceived += OnWebMessage;

            // 读取所有资源并组装完整 HTML（内联 CSS/JS，避免跨域问题）
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            string ReadResource(string name)
            {
                using var s = assembly.GetManifestResourceStream(name);
                if (s == null) return "";
                using var r = new System.IO.StreamReader(s);
                return r.ReadToEnd();
            }

            var css = ReadResource("TaskbarLyrics.Assets.Web.overlay.css");
            var js = ReadResource("TaskbarLyrics.Assets.Web.overlay.js");

            var html = @"<!DOCTYPE html><html lang='zh-CN'><head><meta charset='UTF-8'>
<meta name='viewport' content='width=device-width,initial-scale=1.0'>
<style>" + css + @"</style></head><body>
<div id='root' class='layout-horizontal'>
<div id='coverArea' class='cover-rounded'><img id='coverImg' src='' alt=''><div id='coverPlaceholder'>🎵</div></div>
<div id='contentArea'>
<canvas id='spectrum'></canvas>
<div id='trackInfoLine' class='visible'>等待播放中...</div>
<div id='lyricsArea' class='lyrics-container'>
<div id='unsyncedHint' style='display:none'>无时间戳歌词 · 按节奏估算</div>
<div class='lyric-row' id='rowCurrent'><span class='lyric-bg' id='curBg'></span><span class='lyric-fg' id='curFg'></span></div>
<div class='lyric-row next-row' id='rowNext' style='display:none'><span class='lyric-bg' id='nextBg'></span><span class='lyric-fg' id='nextFg'></span></div>
</div>
</div></div>
<script>" + js + @"</script></body></html>";

            cwv2.NavigateToString(html);
            _webReady = true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("WV2:" + ex.Message); }
    }

    /// <summary>
    /// 圆角已由 HTML 面板的 border-radius 实现（窗口为透明亚克力，四角透出桌面/毛玻璃）。
    /// 保留方法以避免改动调用点；不再设置窗口区域，避免与分层窗口冲突。
    /// </summary>
    private void ApplyRoundedRegion()
    {
        if (_oldRgn != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_oldRgn);
            _oldRgn = IntPtr.Zero;
        }
    }

    // ==================== Positioning ====================

    /// <summary>用户主动隐藏悬浮窗（托盘/设置按钮）：标记手动隐藏，防止意外恢复逻辑误弹。</summary>
    public void HideByUser()
    {
        _userHidden = true;
        Hide();
    }

    /// <summary>用户主动显示悬浮窗（托盘/设置按钮）：清除手动隐藏标记并定位。</summary>
    public void ShowByUser()
    {
        _userHidden = false;
        Show();
        PositionWindow();
    }

    /// <summary>
    /// 保持悬浮窗置顶：每 tick SetWindowPos(HWND_TOPMOST) 确保悬浮窗盖在任务栏之上，
    /// 点击任务栏/开始菜单时不会落到任务栏之下被遮挡（不使用 GWL_HWNDPARENT 锚定，
    /// 因为 owner 关系会导致任务栏隐藏/最小化时悬浮窗跟随一起消失）。
    /// </summary>
    private void EnsureTopmost()
    {
        try
        {
            if (_windowHwnd == IntPtr.Zero) return;
            NativeMethods.SetWindowPos(_windowHwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch { }
    }

    public void PositionWindow()
    {
        var region = TaskbarSpaceDetector.Detect();
        if (region.Taskbar.Equals(default)) return;
        double dpi = _dpiScale > 0 ? _dpiScale : 1.0;
        double tbLeft = region.Taskbar.Left / dpi;
        double tbTop = region.Taskbar.Top / dpi;
        double tbRight = region.Taskbar.Right / dpi;
        double tbBottom = region.Taskbar.Bottom / dpi;
        double taskbarH = tbBottom - tbTop;
        if (taskbarH <= 0) return;

        double emptyLeft = _settings.AutoDetectTaskbarSpace && region.LeftDetected
            ? region.EmptyLeft / dpi : tbLeft + _settings.TaskbarLeftOffset / dpi;
        double emptyRight = _settings.AutoDetectTaskbarSpace && region.RightDetected
            ? region.EmptyRight / dpi : tbRight - 320 / dpi;
        double available = Math.Max(80, emptyRight - emptyLeft);

        // Width — 自动宽度有上限约束，避免撑满任务栏
        double w;
        if (_settings.AutoWidth && _reportedContentW > 40)
        {
            w = Math.Clamp(_reportedContentW + _settings.AutoSizeOffset, 160, 420);
        }
        else
            w = Math.Clamp((tbRight - tbLeft) * _settings.WidthRatio, 160, 420);
        w = Math.Min(w, available);

        // Height — 始终不超过任务栏高度
        double h = taskbarH;
        if (_settings.AutoHeight && _settings.CoverLayout == 1 && _reportedContentH > 20)
        {
            // 上下布局时高度可能大于任务栏高度，限制
            h = Math.Clamp(_reportedContentH + _settings.AutoSizeOffset, taskbarH, taskbarH * 2.5);
        }

        double x = _settings.HorizontalAnchor switch
        {
            1 => emptyLeft + (available - w) / 2,  // center
            2 => emptyRight - w,                   // right
            _ => emptyLeft                          // left
        };
        x += _settings.WindowXOffset / dpi;
        double y = tbTop + _settings.WindowYOffset / dpi;

        Left = x; Top = y; Width = w; Height = h;
        ApplyRoundedRegion();
    }

    public void ApplyThemeAndRelocate()
    {
        if (!IsLoaded) return;
        Topmost = _settings.AlwaysOnTop;
        SendConfig();
        SendLayout();
        PositionWindow();
    }

    /// <summary>立即应用歌词切换动画设置（无需重新保存其它设置）。</summary>
    public void ApplyLyricTransition()
    {
        if (!IsLoaded) return;
        SendConfig();
    }

    // ==================== Main Loop ====================

    private async Task RefreshAsync()
    {
        if (!_webReady) return;
        // 保持悬浮窗置顶（点击任务栏/开始菜单时不被任务栏遮挡，也不随任务栏隐藏而消失）
        EnsureTopmost();
        // 曲目检测（多路兜底：窗口标题 → SMTC → 本地 API，由 TrackDetector 编排）
        var track = await _detector.DetectWithFallbackAsync();

        // ---- SMTC 实时校准 ----
        SmtcMediaService.SmtcSnapshot? snap = null;
        try { snap = _smtc.PollSnapshot(); } catch { }

        var key = track != null
            ? LyricsManager.Normalize($"{track.Title}|{track.Artist}")
            : "";

        // 换歌：重置曲目级状态（同一播放器内保持 SMTC 信任，避免重新验证造成延迟）
        bool newTrack = key != _lastKey;
        if (newTrack)
        {
            _lastKey = key;
            _lastSentIndex = -2;
            _lastSentMode = "";
            _lastSentPlaying = true;
            _lastSentSpec = true;
            // 立即清空旧歌词：获取新歌词期间不再显示上一首的歌词
            _lyrics.Current = null;
            _lyrics.IsInstrumental = false;
            _lastSentLyricsMarker = "";
            // 清空上一首歌的 SMTC 缩略图：避免新歌封面请求误用旧缩略图（SMTC 验证完成后再更新）
            _smtcThumb = null;
            // 切歌即开启校准：重置播放状态辅助信号，避免状态上报滞后导致歌词冻结
            _newTrackAt = DateTime.UtcNow;
            _lastSmtcAdvance = DateTime.UtcNow;
            _prevSmtcPos = null;
        }

        // 会话应用变化 → 失效旧的信任关系，需要重新验证
        if (_verifiedApp != null && snap != null && snap.AppId != _verifiedApp)
        {
            _verifiedApp = null;
            _smtcApp = null;
            _pendingSmtc = null;
        }

        // 无信任会话时异步验证一次（同一应用只验证一次，避免每次切歌都等待）
        if (track != null && snap != null && _smtcApp == null &&
            snap.AppId != _verifiedApp && _pendingSmtc == null)
            _pendingSmtc = _smtc.GetFromSmtcOnlyAsync();
        if (_pendingSmtc is { IsCompleted: true })
        {
            var smtc = _pendingSmtc.Result;
            _pendingSmtc = null;
            _verifiedApp = snap?.AppId ?? smtc?.PlaybackApp;
            if (track != null && smtc != null && TracksMatch(track, smtc))
            {
                _smtcApp = smtc.PlaybackApp;
                _smtcThumb = smtc.ThumbnailBytes; // 保存 SMTC 缩略图,封面失败时兜底
                _lastSmtcTrack = smtc;            // 保存 SMTC 准确曲目信息,优先用于歌词/封面检索
                _detector.CalibrateStart(smtc.Position, smtc.Duration);
            }
            else
            {
                _smtcApp = null;
            }
        }

        // 新歌且有 SMTC 会话：立即用真实进度建立基线，消除“从获取点开始播放”的错位
        if (newTrack && track != null && snap != null)
            _detector.CalibrateStart(snap.Position, snap.EndTime);

        // 观察 SMTC 位置是否在前进（兜底信号：兼容切歌后状态上报滞后的播放器）
        var nowUtc = DateTime.UtcNow;
        if (snap != null)
        {
            if (_prevSmtcPos.HasValue)
            {
                var snapDeltaSec = (snap.Position - _prevSmtcPos.Value).TotalSeconds;
                var snapElapsedSec = _lastSnapAt == DateTime.MinValue ? 0 : (nowUtc - _lastSnapAt).TotalSeconds;
                if (snap.Position > _prevSmtcPos.Value + TimeSpan.FromMilliseconds(30))
                    _lastSmtcAdvance = nowUtc;
                // 用户拖动进度条（向前/向后/循环）：位置增量与真实流逝时间明显不符 → 立即对齐
                if (snapElapsedSec > 0.05 && Math.Abs(snapDeltaSec - snapElapsedSec) > 2.0)
                    _detector.AlignTo(snap.Position);
                // 明确的向后回退（即使 <2s 也响应）：保证“往回拉进度条歌词立即回滚”
                else if (snapDeltaSec < -0.5)
                    _detector.AlignTo(snap.Position);
            }
            _prevSmtcPos = snap.Position;
            _lastSnapAt = nowUtc;
        }
        bool positionAdvancing = (nowUtc - _lastSmtcAdvance) < TimeSpan.FromSeconds(1.5);
        // 切歌宽限期：切换后的 2 秒内一律视为播放中，避免播放器过渡期状态异常导致歌词冻结
        bool inSwitchGrace = (nowUtc - _newTrackAt) < TimeSpan.FromSeconds(2);

        // 暂停判断只信任“已验证与当前曲目一致”的 SMTC 会话（多实例播放器/旧会话不参与，
        // 避免陈旧会话把正在播放的歌误判为暂停导致歌词冻结）；无可信会话时以窗口标题状态为准。
        bool hasSmtcStatus = !string.IsNullOrEmpty(_smtcApp) &&
            snap != null && snap.AppId == _smtcApp;
        bool windowSaysPlaying = track != null &&
            track.PlaybackStatus.Equals("Playing", StringComparison.OrdinalIgnoreCase);

        bool isPlaying;
        if (hasSmtcStatus)
        {
            // SMTC 状态为 Playing、位置在前进、处于切歌宽限期、或窗口标题仍显示播放中 → 视为播放中
            isPlaying = snap!.Status.Equals("Playing", StringComparison.OrdinalIgnoreCase)
                || positionAdvancing
                || inSwitchGrace
                || windowSaysPlaying;
            // 自动校准：跟随媒体会话真实进度（仅限可信会话）
            _detector.Calibrate(snap.Position, isPlaying);
        }
        else if (windowSaysPlaying)
        {
            // 无可信 SMTC 时窗口标题可能不标注暂停（网易云暂停时标题仍是“歌名-歌手”）：
            // 用本地播放器 API 校验真实播放状态（节流 2s；API 不可用时回退到窗口标题判断）
            isPlaying = await QueryLocalPlayingAsync() ?? true;
        }
        else
        {
            isPlaying = false;
        }

        // Player-linked show/hide
        bool hasTrack = track != null;
        _detector.SetPaused(!isPlaying);
        var pos = _detector.GetPosition();
        // 手动校准偏移（正值 = 歌词提前显示，负值 = 歌词延后显示）
        var displayPos = pos + TimeSpan.FromMilliseconds(_settings.LyricOffsetMs);
        if (displayPos < TimeSpan.Zero) displayPos = TimeSpan.Zero;

        if (_settings.PlayerAutoShow && hasTrack && isPlaying && !_wasPlaying)
        {
            Dispatcher.Invoke(() => { if (!IsVisible) Show(); });
        }
        if (_settings.PlayerAutoHide && _wasPlaying && (!hasTrack || !isPlaying))
        {
            if (_stoppedAt == default) _stoppedAt = DateTime.UtcNow;
            if ((DateTime.UtcNow - _stoppedAt).TotalSeconds >= 3)
            {
                Dispatcher.Invoke(() => { if (IsVisible) Hide(); });
                _wasPlaying = isPlaying;
                return;
            }
        }
        else _stoppedAt = default;
        _wasPlaying = isPlaying;

        // 意外隐藏恢复：悬浮窗因外部因素（任务栏隐藏/系统干预/菜单弹出）意外消失时自动恢复；
        // 用户手动隐藏（_userHidden）或播放停止（AutoHide 隐藏）不受影响
        if (!_userHidden && !IsVisible && hasTrack && isPlaying)
        {
            Dispatcher.Invoke(() => { Show(); PositionWindow(); });
        }

        if (!IsVisible) return;

        if (track == null)
        {
            _specShouldSend = false;
            _lastSentMode = "idle";
            _lastSentIndex = -1;
            _lastSentPlaying = false;
            _lastSentSpec = false;
            SendState("idle", 0, -1, 0, "paused", false);
            return;
        }

        if (newTrack)
        {
            // 1. 立即发送新曲目信息；获取歌词期间前端保持 loading，不显示旧歌词
            Dispatcher.Invoke(() => { SendTrack(track, null); SendState("loading", 0, -1, 0, "loading", false); });

            // 2. Lyrics search (blocking) + cover (fire-and-forget, updates later)
            // 统一用窗口识别曲目检索，保证歌词缓存键一致（已缓存歌曲即时命中，避免切歌变慢）
            _fetchingLyrics++;
            Models.LyricsData? lyrics = null;
            try { lyrics = await _lyrics.GetLyricsAsync(track); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Lyrics: " + ex.Message); }
            finally { _fetchingLyrics--; }
            if (key != _lastKey) return; // 歌曲已在获取期间切换，丢弃过期结果

            byte[]? coverBytes = null;
            if (_settings.ShowCoverArt && $"{track.Title}|{track.Artist}" != _lastCoverKey)
            {
                _lastCoverKey = $"{track.Title}|{track.Artist}";
                int reqSeq = ++_coverReqSeq; // 本曲目的封面请求序号（旧序号结果到达时丢弃）
                var capTrack = track; // capture for closure
                var capCoverKey = $"{track.Title}|{track.Artist}"; // 发起时的曲目标识
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // 封面附带 SMTC 缩略图,本地/在线失败时作为兜底
                        var coverTrack = new MediaTrack(capTrack.Title, capTrack.Artist, capTrack.Album,
                            capTrack.PlaybackApp, capTrack.PlaybackStatus, capTrack.Position, capTrack.Duration, _smtcThumb);
                        var cb = await _coverArt.GetCoverAsync(coverTrack, _lyrics.AudioFiles, _settings.CoverSourceStrategy);
                        // 乱序保护：仅“曲目已切换”时才要求序号匹配（丢弃旧歌封面，避免旧封面覆盖新封面）；
                        // 曲目未变（页面重载/ready 重置导致重复请求使序号递增）时直接显示，
                        // 避免封面请求被序号反复淘汰而“永不加载”。
                        if (cb == null) { CoverLog($"null: {capTrack.Title}|{capTrack.Artist} seq {reqSeq}/{_coverReqSeq} audio {_lyrics.AudioFiles.Count}"); return; }
                        if (capCoverKey != _lastCoverKey && reqSeq != _coverReqSeq) { CoverLog($"stale: {capCoverKey} seq {reqSeq}/{_coverReqSeq} now {_lastCoverKey}"); return; }
                        CoverLog($"ok: {capCoverKey} {cb.Length}B seq {reqSeq}/{_coverReqSeq}");
                        Dispatcher.Invoke(() => SendCover(cb));
                        // 封面取色:异步从封面提取色板并应用(不阻塞歌词/封面主流程)
                        if (_settings.ExtractCoverThemeColor)
                        {
                            var pal = ThemeColorExtractor.ExtractPalette(cb);
                            if (pal != null)
                            {
                                _lastPalette = pal;
                                Dispatcher.Invoke(() => SendConfig(pal));
                            }
                        }
                    }
                    catch (Exception ex) { CoverLog("EX " + ex.Message); System.Diagnostics.Debug.WriteLine("Cover: " + ex.Message); }
                });
            }

            // Clear stale lyrics if none found for new song
            if (lyrics == null || lyrics.IsEmpty)
            {
                _lyrics.Current = null;
                _lyrics.IsInstrumental = true;
            }

            // 动态主题:新歌先回退系统主题;封面异步取色完成后 SendConfig(palette) 应用色板
            _lastPalette = null;

            Dispatcher.Invoke(() =>
            {
                SendConfig();
                SendLayout();
                SendTrack(track, coverBytes);
                if (lyrics is { IsEmpty: false })
                {
                    SendLyrics(lyrics);
                    _lastSentLyricsMarker = $"{key}|{lyrics.Source}";
                }
            });
        }

        Dispatcher.Invoke(() =>
        {
            // 歌词尚未就绪：保持 loading（仅显示歌名/歌手），避免把旧歌词/错误模式推给前端
            if (_fetchingLyrics > 0)
            {
                SendState("loading", 0, -1, 0, "loading", false);
                return;
            }

            string mode; int index = -1; double progress = 0; int startMs = 0, endMs = 0;
            bool showSpec = false;

            if (_lyrics.Current is { IsEmpty: false })
            {
                mode = "lyrics";
                // 后台在线歌词/升级结果到达时自动推送（同一首歌曲源变化 → 重新下发歌词）
                var marker = $"{key}|{_lyrics.Current.Source}";
                if (marker != _lastSentLyricsMarker)
                {
                    _lastSentLyricsMarker = marker;
                    SendLyrics(_lyrics.Current);
                }
                var cur = _lyrics.Current.GetLineAt(displayPos);
                if (cur != null)
                {
                    index = _lyrics.Current.Lines.IndexOf(cur);
                    progress = _lyrics.GetLineProgress(displayPos);
                    startMs = (int)cur.Time.TotalMilliseconds;
                    var nxt = _lyrics.Current.GetNextLineAt(displayPos);
                    endMs = (int)(nxt?.Time.TotalMilliseconds ?? cur.Time.TotalMilliseconds + 4000);
                }
                showSpec = isPlaying && _settings.ShowSpectrum && _settings.SpectrumWithLyrics;
            }
            else if (_lyrics.IsInstrumental)
            {
                mode = "wave";
                showSpec = isPlaying && _settings.ShowSpectrum && _settings.SpectrumForInstrumental;
            }
            else if (isPlaying && _settings.ShowTrackInfo && track.Title.Length > 0)
            {
                mode = "trackinfo";
                showSpec = isPlaying && _settings.ShowSpectrum && _settings.SpectrumWhenNoLyrics;
            }
            else if (isPlaying)
            {
                mode = "wave";
                showSpec = isPlaying && _settings.ShowSpectrum && _settings.SpectrumWhenNoLyrics;
            }
            else { mode = "idle"; }

            // 频谱暂停淡出：关闭后保留约 0.6s 渲染窗口，让前端平滑衰减再隐藏（避免频谱突然消失/暂停后仍跳动）
            if (showSpec) _specFadeEnd = default;
            else if (_specShouldSend && _specFadeEnd == default) _specFadeEnd = DateTime.UtcNow.AddSeconds(0.6);
            if (!showSpec && _specFadeEnd != default && DateTime.UtcNow < _specFadeEnd) showSpec = true;
            _specShouldSend = showSpec;
            bool playingState = isPlaying;

            _lastSentIndex = index; _lastSentMode = mode;
            _lastSentPlaying = playingState; _lastSentSpec = showSpec;
            SendState(mode, (int)displayPos.TotalMilliseconds, index, progress,
                playingState ? "playing" : "paused", showSpec, startMs, endMs);
        });
    }

    /// <summary>判断 SMTC 会话应用是否为常见音乐播放器（用于暂停状态判断的兜底）。</summary>
    private static bool IsKnownMusicApp(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return false;
        var id = appId.ToLowerInvariant();
        return id.Contains("qqmusic") || id.Contains("cloudmusic") || id.Contains("neteasemusic")
            || id.Contains("kugou") || id.Contains("kuwo") || id.Contains("kwmusic")
            || id.Contains("spotify") || id.Contains("wmplayer") || id.Contains("mediaplayer")
            || id.Contains("foobar") || id.Contains("musicbee") || id.Contains("aimp")
            || id.Contains("itunes") || id.Contains("applemusic") || id.Contains("migu")
            || id.Contains("doubanfm");
    }

    /// <summary>
    /// 无可信 SMTC 时，用本地播放器 API 校验真实播放状态（网易云暂停时窗口标题不标注暂停）。
    /// 节流：2 秒内不重复查询；本地 API 不可用时返回 null（调用方回退到窗口标题判断）。
    /// </summary>
    private async Task<bool?> QueryLocalPlayingAsync()
    {
        var now = DateTime.UtcNow;
        if (_localApiCheckedAt != DateTime.MinValue &&
            (now - _localApiCheckedAt).TotalSeconds < 2.0)
            return _localApiPlaying;
        _localApiCheckedAt = now;
        try
        {
            var t = await _localApi.GetTrackAsync().ConfigureAwait(false);
            _localApiPlaying = t == null
                ? null
                : t.PlaybackStatus.Equals("Playing", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            _localApiPlaying = null;
        }
        return _localApiPlaying;
    }

    /// <summary>判断 SMTC 会话曲目与窗口识别曲目是否一致（归一化标题/歌手比对）。</summary>
    private static bool TracksMatch(MediaTrack a, MediaTrack b)
    {
        if (a == null || b == null) return false;
        var t1 = LyricsManager.Normalize(a.Title);
        var t2 = LyricsManager.Normalize(b.Title);
        if (t1.Length == 0 || t2.Length == 0) return false;

        if (t1 == t2) return true; // 歌名一致
        if (t1.Contains(t2, StringComparison.Ordinal) || t2.Contains(t1, StringComparison.Ordinal))
            return true; // 歌名互相包含

        // 歌名+歌手整体比对（容忍“歌手-歌名”等顺序差异）
        var ta = LyricsManager.Normalize(a.Title + a.Artist);
        var tb = LyricsManager.Normalize(b.Title + b.Artist);
        return ta.Length > 0 && tb.Length > 0 &&
               (ta == tb ||
                ta.Contains(tb, StringComparison.Ordinal) ||
                tb.Contains(ta, StringComparison.Ordinal));
    }

    // ==================== CompositionTarget.Rendering (spectrum + auto-size) ====================

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_webReady || !IsVisible || !_specShouldSend) return;

        _specFrame++;
        // Throttle: send at ~30 fps when playing, ~15 fps when paused
        int interval = _wasPlaying ? 2 : 4;
        if (_specFrame - _specLastSentFrame < interval) return;
        _specLastSentFrame = _specFrame;

        if (_wasPlaying)
        {
            double elapsedSec = _detector.GetPosition().TotalSeconds;
            double beatPhase = elapsedSec * 2.0;
            double beat = 0.4 + 0.6 * Math.Max(0, Math.Sin(beatPhase * Math.PI));
            double bassBeat = 0.25 + 0.75 * Math.Max(0, Math.Sin(beatPhase * Math.PI * 2));

            for (int i = 0; i < 24; i++)
            {
                double fNorm = i / 23.0;
                double bassWeight = Math.Exp(-fNorm * 3.0);
                double trebleWeight = Math.Pow(fNorm, 2.0);
                double midWeight = 1.0 - Math.Abs(fNorm - 0.4) * 2.0;
                if (midWeight < 0) midWeight = 0;

                double noise = _rng.NextDouble();
                double target = 0.08
                    + 0.85 * bassWeight * bassBeat * (0.6 + 0.4 * noise)
                    + 0.70 * midWeight * beat * (0.5 + 0.5 * noise)
                    + 0.50 * trebleWeight * (0.3 + 0.7 * noise) * beat;

                _specTargets[i] = (float)Math.Clamp(target, 0.02, 1.0);
            }

            float resp = (float)_settings.SpectrumResponse;
            for (int i = 0; i < 24; i++)
                _specBands[i] += (_specTargets[i] - _specBands[i]) * resp;
        }
        else
        {
            // Fade when paused（暂停淡出：快速衰减，0.6s 淡出窗口内由 _specShouldSend 保持发送）
            for (int i = 0; i < 24; i++)
                _specBands[i] *= 0.78f;
        }

        var bands = new float[24];
        Array.Copy(_specBands, bands, 24);
        Dispatcher.BeginInvoke(() => SendSpectrumData(bands));
    }

    // ==================== C# → JS ====================

    // 动态主题：封面取色色板（null = 系统主题兜底）
    private ThemePalette? _lastPalette;
    // SMTC 提供的封面缩略图（本地/在线封面失败时的兜底）
    private byte[]? _smtcThumb;
    // SMTC 提供的准确曲目信息（标题/歌手/时长，优先用于歌词检索与封面匹配）
    private MediaTrack? _lastSmtcTrack;

    private void SendConfig(ThemePalette? palette = null)
    {
        var active = palette ?? _lastPalette;
        PostMessage("config", new Dictionary<string, object?>
        {
            ["textColor"] = "#" + _settings.TextColor.TrimStart('#'),
            ["bgColor"] = "#" + _settings.BackgroundColor.TrimStart('#'),
            ["waveColor"] = "#" + _settings.WaveColor.TrimStart('#'),
            ["accentColor"] = (active?.Accent ?? "#" + _settings.WaveColor.TrimStart('#')),
            ["palette"] = active == null ? null : new Dictionary<string, object?>
            {
                ["primary"] = active.Primary,
                ["accent"] = active.Accent,
                ["surfaceRgb"] = active.SurfaceRgb,
                ["textPrimary"] = active.TextPrimary,
                ["textSecondary"] = active.TextSecondary
            },
            ["fontSize"] = _settings.FontSize,
            ["fontFamily"] = _settings.FontFamily,
            ["showCover"] = _settings.ShowCoverArt,
            ["coverStyle"] = _settings.CoverStyle,
            ["coverSize"] = _settings.CoverSize,
            ["backgroundEnabled"] = _settings.BackgroundEnabled,
            ["bgOpacity"] = _settings.BackgroundOpacity,
            ["borderEnabled"] = _settings.BorderEnabled,
            ["borderColor"] = "#" + _settings.BorderColor.TrimStart('#'),
            ["textShadow"] = _settings.TextShadow,
            ["showSpectrum"] = _settings.ShowSpectrum,
            ["spectrumOpacity"] = _settings.SpectrumOpacity,
            ["spectrumHeightRatio"] = _settings.SpectrumHeightRatio,
            ["spectrumStyle"] = _settings.SpectrumStyle,
            ["spectrumResponse"] = _settings.SpectrumResponse,
            ["spectrumRefreshMs"] = _settings.SpectrumRefreshMs,
            ["lyricTransition"] = _settings.LyricTransition,
            ["tbCoverXOffset"] = _settings.TbCoverXOffset,
            ["tbCoverYOffset"] = _settings.TbCoverYOffset,
            ["tbContentXOffset"] = _settings.TbContentXOffset,
            ["tbContentYOffset"] = _settings.TbContentYOffset,
            ["tbCoverToTrackSpacing"] = _settings.TbCoverToTrackSpacing,
            ["tbCoverToContentSpacing"] = _settings.TbCoverToContentSpacing
        });
    }

    private void SendLayout()
    {
        PostMessage("layout", new Dictionary<string, object?>
        {
            ["coverLayout"] = _settings.CoverLayout,
            ["coverSize"] = _settings.CoverSize,
            ["topBottomShowTrackInfo"] = _settings.TopBottomShowTrackInfo,
            ["tbCoverXOffset"] = _settings.TbCoverXOffset,
            ["tbCoverYOffset"] = _settings.TbCoverYOffset,
            ["tbContentXOffset"] = _settings.TbContentXOffset,
            ["tbContentYOffset"] = _settings.TbContentYOffset,
            ["tbCoverToTrackSpacing"] = _settings.TbCoverToTrackSpacing,
            ["tbCoverToContentSpacing"] = _settings.TbCoverToContentSpacing,
        });
    }

    private void SendCover(byte[]? coverBytes)
    {
        PostMessage("cover", new Dictionary<string, object?>
        {
            ["cover"] = _coverArt.ToDataUri(coverBytes)
        });
    }

    private void SendTrack(MediaTrack track, byte[]? coverBytes)
    {
        PostMessage("track", new Dictionary<string, object?>
        {
            ["title"] = track.Title,
            ["artist"] = track.Artist,
            ["album"] = track.Album,
            ["cover"] = _coverArt.ToDataUri(coverBytes)
        });
    }

    private void SendLyrics(LyricsData lyrics)
    {
        var lineArr = lyrics.Lines.Select(l => new { t = (int)l.Time.TotalMilliseconds, x = l.Text }).ToArray();
        PostMessage("lyrics", new Dictionary<string, object?>
        {
            ["lines"] = lineArr,
            ["source"] = lyrics.Source,
            ["synced"] = lyrics.IsSynced   // 纯文本歌词(无时间戳,按节奏估算)为 false
        });
    }

    private void SendState(string mode, int positionMs, int index, double progress,
        string status, bool showSpectrum, int startMs = 0, int endMs = 0)
    {
        PostMessage("state", new Dictionary<string, object?>
        {
            ["mode"] = mode, ["status"] = status, ["positionMs"] = positionMs,
            ["index"] = index, ["progress"] = progress,
            ["startMs"] = startMs, ["endMs"] = endMs,
            ["showSpectrum"] = showSpectrum
        });
    }

    private void SendProgress(double progress, int posMs, int startMs, int endMs)
    {
        PostMessage("progress", new Dictionary<string, object?>
        {
            ["p"] = Math.Round(progress, 3),
            ["pos"] = posMs,
            ["s"] = startMs,
            ["e"] = endMs
        });
    }

    private void SendSpectrumData(float[] bands)
    {
        PostMessage("spectrum", new Dictionary<string, object?> { ["bands"] = bands });
    }

    private void PostMessage(string type, object payload)
    {
        if (!_webReady) return;
        try
        {
            var dict = payload as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            dict["type"] = type;
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(dict));
        }
        catch { }
    }

    /// <summary>封面调试日志（写在 exe 旁 cover_debug.log，用于定位封面不加载问题）。</summary>
    private static void CoverLog(string msg)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppContext.BaseDirectory, "cover_debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // ==================== JS → C# ====================

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(json)) return;
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : "";
            switch (type)
            {
                case "ready":
                    // 页面就绪：推送配置 + 强制下次 tick 重新获取所有数据
                    Dispatcher.Invoke(() => { SendConfig(); SendLayout(); _lastKey = ""; _lastCoverKey = ""; });
                    break;
                case "sizeReport":
                    var w = doc.RootElement.TryGetProperty("width", out var jw) ? jw.GetDouble() : 0;
                    var h = doc.RootElement.TryGetProperty("height", out var jh) ? jh.GetDouble() : 0;
                    if (w > 0) _reportedContentW = w;
                    if (h > 0) _reportedContentH = h;
                    if (_settings.AutoWidth || _settings.AutoHeight)
                        Dispatcher.BeginInvoke(() => PositionWindow());
                    break;
            }
        }
        catch { }
    }
}
