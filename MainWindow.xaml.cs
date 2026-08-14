using System.Windows;
using System.Windows.Controls;
using TaskbarLyrics.Services;

namespace TaskbarLyrics;

/// <summary>设置窗口：标签页式 UI，涵盖显示、封面、频谱、窗口和高级设置。</summary>
public partial class MainWindow : Window
{
    // CompositionTarget.Rendering 替代 DispatcherTimer：与屏幕刷新率同步触发，
    // 用时间节流保证每秒最多刷新一次预览（避免无谓的全进程扫描）。
    private DateTime _lastPreviewRefresh = DateTime.MinValue;
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromSeconds(1);
    private readonly IMediaService _smtc;

    public MainWindow(IMediaService smtc)
    {
        _smtc = smtc;
        InitializeComponent();
        LoadSettings();
        WireSliders();

        // 预览刷新：CompositionTarget.Rendering（每帧触发）→ 节流到 ~1s 一次。
        // 与屏幕刷新率同步：窗口隐藏时 Rendering 不再触发，预览自然停止（无需 IsVisible 判断）。
        System.Windows.Media.CompositionTarget.Rendering += OnRenderingFrame;
        Closed += (_, _) => System.Windows.Media.CompositionTarget.Rendering -= OnRenderingFrame;
    }

    private void OnRenderingFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastPreviewRefresh) < PreviewInterval) return;
        _lastPreviewRefresh = now;
        _ = RefreshPreviewAsync(); // fire-and-forget：刷新失败不影响渲染循环
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

    // ==================== 颜色选择（色盘） ====================

    /// <summary>点击“选择…”按钮：打开系统色盘，选色后写回文本框并刷新预览。</summary>
    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string boxName) return;
        var tb = FindName(boxName) as System.Windows.Controls.TextBox;
        if (tb == null) return;

        using var dlg = new System.Windows.Forms.ColorDialog();
        if (TryParseHex(tb.Text, out var current))
            dlg.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            tb.Text = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
    }

    /// <summary>点击颜色预览色块：等效于点击“选择…”。</summary>
    private void Swatch_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string boxName)
        {
            var btnName = "BtnPick" + boxName.Replace("Txt", "");
            if (FindName(btnName) is System.Windows.Controls.Button btn)
                PickColor_Click(btn, new RoutedEventArgs());
        }
    }

    /// <summary>文本框内容变化：实时刷新色块预览（输入合法 #RRGGBB 时）。</summary>
    private void ColorText_TextChanged(object sender, TextChangedEventArgs e)
    {
        // TextBox 的 Tag 直接指向对应色块名（如 TxtColor 的 Tag="SwColor"）
        if (sender is not FrameworkElement fe || fe.Tag is not string swatchName) return;
        if (FindName(swatchName) is not Border swatch) return;
        swatch.Background = TryParseHex((sender as System.Windows.Controls.TextBox)?.Text, out var c)
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(c.R, c.G, c.B))
            : System.Windows.Media.Brushes.Transparent;
    }

    /// <summary>解析 #RRGGBB（或 #RGB）十六进制颜色，失败返回 false。</summary>
    private static bool TryParseHex(string? text, out System.Drawing.Color color)
    {
        color = System.Drawing.Color.White;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 6 &&
            int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            color = System.Drawing.Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            return true;
        }
        if (hex.Length == 3 &&
            int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb3))
        {
            int r = (rgb3 >> 8) & 0xF, g = (rgb3 >> 4) & 0xF, b = rgb3 & 0xF;
            color = System.Drawing.Color.FromArgb(r | (r << 4), g | (g << 4), b | (b << 4));
            return true;
        }
        return false;
    }

    // ==================== 状态预览 ====================

    /// <summary>刷新播放状态预览（Rendering 事件节流触发；窗口隐藏时不触发，避免后台反复全进程扫描拖垮 UI 线程）。</summary>
    private async Task RefreshPreviewAsync()
    {
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
