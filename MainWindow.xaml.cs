using System.Windows;
using System.Windows.Controls;
using TaskbarLyrics.Services;

namespace TaskbarLyrics;

/// <summary>设置窗口：标签页式 UI，涵盖显示、封面、频谱、窗口和高级设置。</summary>
public partial class MainWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly IMediaService _smtc;

    public MainWindow(IMediaService smtc)
    {
        _smtc = smtc;
        InitializeComponent();
        LoadSettings();
        WireSliders();

        // 每秒刷新一次播放状态预览
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    // ==================== 加载设置 ====================

    /// <summary>将 AppSettings 的值加载到各控件。</summary>
    private void LoadSettings()
    {
        var s = App.Settings;

        // 显示
        SldFont.Value = s.FontSize;
        ChkOnline.IsChecked = s.EnableOnline;
        ChkTrackInfo.IsChecked = s.ShowTrackInfo;
        ChkTextShadow.IsChecked = s.TextShadow;
        TxtColor.Text = s.TextColor;
        TxtWaveColor.Text = s.WaveColor;
        TxtBgColor.Text = s.BackgroundColor;
        CmbLyricTransition.SelectedIndex = Math.Clamp(s.LyricTransition, 0, 3);
        // 即时生效：切换动画选项无需保存，立即应用（LoadSettings 期间 IsLoaded=false 自动跳过）
        CmbLyricTransition.SelectionChanged += (_, _) =>
        {
            if (IsLoaded && CmbLyricTransition.SelectedIndex >= 0)
            {
                App.Settings.LyricTransition = CmbLyricTransition.SelectedIndex;
                App.Overlay.ApplyLyricTransition();
            }
        };
        CmbPosition.SelectedIndex = Math.Clamp(s.Position, 0, 2);
        SldLyricOffset.Value = Math.Clamp(s.LyricOffsetMs, -3000, 3000);
        TxtLyricOffset.Text = $"歌词同步偏移: {s.LyricOffsetMs}ms";

        // 封面
        ChkShowCover.IsChecked = s.ShowCoverArt;
        CmbCoverStyle.SelectedIndex = Math.Clamp(s.CoverStyle, 0, 2);
        SldCoverSize.Value = s.CoverSize;
        CmbCoverLayout.SelectedIndex = Math.Clamp(s.CoverLayout, 0, 1);
        ChkCoverCrossFade.IsChecked = s.CoverCrossFade;
        ChkExtractColor.IsChecked = s.ExtractCoverThemeColor;
        CmbCoverStrategy.SelectedIndex = Math.Clamp(s.CoverSourceStrategy, 0, 3);

        // 频谱
        ChkShowSpectrum.IsChecked = s.ShowSpectrum;
        ChkSpecInstrumental.IsChecked = s.SpectrumForInstrumental;
        ChkSpecNoLyrics.IsChecked = s.SpectrumWhenNoLyrics;
        ChkSpecWithLyrics.IsChecked = s.SpectrumWithLyrics;
        CmbSpectrumStyle.SelectedIndex = Math.Clamp(s.SpectrumStyle, 0, 5);
        SldSpecResponse.Value = s.SpectrumResponse;
        SldSpecHeight.Value = s.SpectrumHeightRatio;
        SldSpecOpacity.Value = s.SpectrumOpacity;
        SldSpecRefresh.Value = s.SpectrumRefreshMs;

        // 窗口
        ChkAutoWidth.IsChecked = s.AutoWidth;
        ChkAutoHeight.IsChecked = s.AutoHeight;
        ChkAlwaysOnTop.IsChecked = s.AlwaysOnTop;
        ChkBackground.IsChecked = s.BackgroundEnabled;
        ChkBorder.IsChecked = s.BorderEnabled;
        CmbAnchor.SelectedIndex = Math.Clamp(s.HorizontalAnchor, 0, 2);
        SldManualWidth.Value = s.ManualWidth;
        SldXOffset.Value = s.WindowXOffset;
        SldYOffset.Value = s.WindowYOffset;

        // 高级
        ChkPlayerAutoShow.IsChecked = s.PlayerAutoShow;
        ChkPlayerAutoHide.IsChecked = s.PlayerAutoHide;
        ChkAutoStart.IsChecked = s.AutoStart;
        ChkAutoSpace.IsChecked = s.AutoDetectTaskbarSpace;
        ChkPlayerCache.IsChecked = s.EnablePlayerCache;
        TxtCacheFolders.Text = string.Join("\r\n", s.PlayerCacheFolders);
        TxtFolders.Text = string.Join("\r\n", s.MusicFolders);
    }

    /// <summary>绑定滑块事件显示数值标签。</summary>
    private void WireSliders()
    {
        SldCoverSize.ValueChanged += (_, _) =>
            TxtCoverSize.Text = $"封面大小: {(int)SldCoverSize.Value}px";
        SldManualWidth.ValueChanged += (_, _) =>
            TxtManualWidth.Text = $"手动宽度: {(int)SldManualWidth.Value}px";
        SldXOffset.ValueChanged += (_, _) =>
            TxtXOffset.Text = $"X 偏移: {(int)SldXOffset.Value}px";
        SldYOffset.ValueChanged += (_, _) =>
            TxtYOffset.Text = $"Y 偏移: {(int)SldYOffset.Value}px";
        SldLyricOffset.ValueChanged += (_, _) =>
            TxtLyricOffset.Text = $"歌词同步偏移: {(int)SldLyricOffset.Value}ms（正数=歌词提前显示）";
        SldSpecResponse.ValueChanged += (_, _) =>
            TxtSpecResponse.Text = $"响应速度: {SldSpecResponse.Value:F2}（越小越平滑，越大越灵敏）";
        SldSpecHeight.ValueChanged += (_, _) =>
            TxtSpecHeight.Text = $"高度范围: {SldSpecHeight.Value:F2}（相对容器高度）";
        SldSpecOpacity.ValueChanged += (_, _) =>
            TxtSpecOpacity.Text = $"不透明度: {SldSpecOpacity.Value:F2}";
        SldSpecRefresh.ValueChanged += (_, _) =>
            TxtSpecRefresh.Text = $"刷新间隔: {(int)SldSpecRefresh.Value} ms";

        // 触发初始显示
        TxtCoverSize.Text = $"封面大小: {(int)SldCoverSize.Value}px";
        TxtManualWidth.Text = $"手动宽度: {(int)SldManualWidth.Value}px";
        TxtXOffset.Text = $"X 偏移: {(int)SldXOffset.Value}px";
        TxtYOffset.Text = $"Y 偏移: {(int)SldYOffset.Value}px";
        TxtSpecResponse.Text = $"响应速度: {SldSpecResponse.Value:F2}（越小越平滑，越大越灵敏）";
        TxtSpecHeight.Text = $"高度范围: {SldSpecHeight.Value:F2}（相对容器高度）";
        TxtSpecOpacity.Text = $"不透明度: {SldSpecOpacity.Value:F2}";
        TxtSpecRefresh.Text = $"刷新间隔: {(int)SldSpecRefresh.Value} ms";
    }

    // ==================== 状态预览 ====================

    /// <summary>定时刷新播放状态预览（窗口隐藏时跳过，避免后台反复全进程扫描拖垮 UI 线程）。</summary>
    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible) return; // 设置窗口隐藏时不扫描，防止无谓的全进程枚举
        try
        {
            var track = await _smtc.GetCurrentTrackAsync();
            if (track == null)
                TxtStatus.Text = "当前没有检测到播放中的音乐";
            else
            {
                var info = App.Lyrics.Current != null
                    ? $"  歌词来源: {App.Lyrics.Current.Source}"
                    : "  无歌词";
                TxtStatus.Text = $"播放器: {track.PlaybackApp}  状态: {track.PlaybackStatus}\n" +
                                 $"歌曲: {track.Title} - {track.Artist}{info}";
            }
        }
        catch (Exception ex) { TxtStatus.Text = "错误: " + ex.Message; }
    }

    // ==================== 缓存按钮 ====================

    /// <summary>填入常见播放器缓存目录。</summary>
    private void BtnDetectCache_Click(object sender, RoutedEventArgs e)
    {
        TxtCacheFolders.Text = string.Join("\r\n", PlayerLyricsCache.DefaultCacheFolders);
    }

    // ==================== 保存 ====================

    /// <summary>保存所有设置到文件并应用到悬浮窗。</summary>
    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;

        // 显示
        s.FontSize = (int)SldFont.Value;
        s.EnableOnline = ChkOnline.IsChecked == true;
        s.ShowTrackInfo = ChkTrackInfo.IsChecked == true;
        s.TextShadow = ChkTextShadow.IsChecked == true;
        s.TextColor = string.IsNullOrWhiteSpace(TxtColor.Text) ? s.TextColor : TxtColor.Text.Trim();
        s.WaveColor = string.IsNullOrWhiteSpace(TxtWaveColor.Text) ? s.WaveColor : TxtWaveColor.Text.Trim();
        s.BackgroundColor = string.IsNullOrWhiteSpace(TxtBgColor.Text) ? s.BackgroundColor : TxtBgColor.Text.Trim();
        s.LyricTransition = CmbLyricTransition.SelectedIndex;
        s.Position = CmbPosition.SelectedIndex;
        s.LyricOffsetMs = (int)SldLyricOffset.Value;

        // 封面
        s.ShowCoverArt = ChkShowCover.IsChecked == true;
        s.CoverStyle = CmbCoverStyle.SelectedIndex;
        s.CoverSize = (int)SldCoverSize.Value;
        s.CoverLayout = CmbCoverLayout.SelectedIndex;
        s.CoverCrossFade = ChkCoverCrossFade.IsChecked == true;
        s.ExtractCoverThemeColor = ChkExtractColor.IsChecked == true;
        s.CoverSourceStrategy = CmbCoverStrategy.SelectedIndex;

        // 频谱
        s.ShowSpectrum = ChkShowSpectrum.IsChecked == true;
        s.SpectrumForInstrumental = ChkSpecInstrumental.IsChecked == true;
        s.SpectrumWhenNoLyrics = ChkSpecNoLyrics.IsChecked == true;
        s.SpectrumWithLyrics = ChkSpecWithLyrics.IsChecked == true;
        s.SpectrumStyle = CmbSpectrumStyle.SelectedIndex;
        s.SpectrumResponse = Math.Round(SldSpecResponse.Value, 2);
        s.SpectrumHeightRatio = Math.Round(SldSpecHeight.Value, 2);
        s.SpectrumOpacity = Math.Round(SldSpecOpacity.Value, 2);
        s.SpectrumRefreshMs = (int)SldSpecRefresh.Value;

        // 窗口
        s.AutoWidth = ChkAutoWidth.IsChecked == true;
        s.AutoHeight = ChkAutoHeight.IsChecked == true;
        s.AlwaysOnTop = ChkAlwaysOnTop.IsChecked == true;
        s.BackgroundEnabled = ChkBackground.IsChecked == true;
        s.BorderEnabled = ChkBorder.IsChecked == true;
        s.HorizontalAnchor = CmbAnchor.SelectedIndex;
        s.ManualWidth = (int)SldManualWidth.Value;
        s.WindowXOffset = (int)SldXOffset.Value;
        s.WindowYOffset = (int)SldYOffset.Value;

        // 高级
        s.PlayerAutoShow = ChkPlayerAutoShow.IsChecked == true;
        s.PlayerAutoHide = ChkPlayerAutoHide.IsChecked == true;
        s.AutoStart = ChkAutoStart.IsChecked == true;
        s.AutoDetectTaskbarSpace = ChkAutoSpace.IsChecked == true;
        s.EnablePlayerCache = ChkPlayerCache.IsChecked == true;
        s.PlayerCacheFolders = SplitLines(TxtCacheFolders.Text);
        s.MusicFolders = SplitLines(TxtFolders.Text);

        // 应用
        s.Save();
        App.Lyrics.MusicFolders = s.MusicFolders.Count > 0 ? s.MusicFolders.ToArray() : LyricsManager.DefaultMusicFolders;
        App.Lyrics.PlayerCache.Enabled = s.EnablePlayerCache;
        App.Lyrics.PlayerCache.CacheFolders = s.PlayerCacheFolders.Count > 0 ? s.PlayerCacheFolders.ToArray() : PlayerLyricsCache.DefaultCacheFolders;
        App.Lyrics.ResetIndex();
        App.Overlay.ApplyThemeAndRelocate();

        System.Windows.MessageBox.Show("设置已保存并生效", "任务栏歌词",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>隐藏按钮：切换悬浮窗显示/隐藏（标记为手动操作，不影响意外恢复逻辑）。</summary>
    private void BtnHide_Click(object sender, RoutedEventArgs e)
    {
        var ov = App.Overlay;
        if (ov.IsVisible) ov.HideByUser();
        else ov.ShowByUser();
    }

    private static List<string> SplitLines(string text) =>
        text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
}
