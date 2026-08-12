using System.IO;
using System.Text.Json;

namespace TaskbarLyrics.Helpers;

public class AppSettings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarLyrics");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    // ==================== Position ====================
    public int Position { get; set; } = 2;
    public double WidthRatio { get; set; } = 0.22;
    public int FontSize { get; set; } = 14;
    public bool EnableOnline { get; set; } = true;
    public bool ShowTrackInfo { get; set; } = true;

    // ==================== Cover ====================
    public bool ShowCoverArt { get; set; } = true;
    public int CoverStyle { get; set; } = 1;
    public int CoverSize { get; set; } = 40;
    public int CoverLayout { get; set; } = 0;
    public int CoverSourceStrategy { get; set; } = 0;
    public bool CoverCrossFade { get; set; } = true;

    // ==================== Top-bottom layout ====================
    public bool TopBottomShowTrackInfo { get; set; } = true;
    public int TbCoverToTrackSpacing { get; set; } = 4;
    public int TbCoverToContentSpacing { get; set; } = 8;
    public int TbCoverXOffset { get; set; } = 0;
    public int TbCoverYOffset { get; set; } = 0;
    public int TbContentXOffset { get; set; } = 0;
    public int TbContentYOffset { get; set; } = 0;

    // ==================== Theme color ====================
    public bool ExtractCoverThemeColor { get; set; } = true;

    // ==================== Spectrum ====================
    public bool ShowSpectrum { get; set; } = true;
    public bool SpectrumForInstrumental { get; set; } = true;
    public bool SpectrumWhenNoLyrics { get; set; } = true;
    public bool SpectrumWithLyrics { get; set; } = false;
    public int SpectrumStyle { get; set; } = 0;
    public double SpectrumResponse { get; set; } = 0.65;
    public double SpectrumHeightRatio { get; set; } = 0.9;
    public double SpectrumOpacity { get; set; } = 0.85;
    public int SpectrumRefreshMs { get; set; } = 33;

    // ==================== Window ====================
    public bool AutoWidth { get; set; } = true;
    public bool AutoHeight { get; set; } = true;
    public int ManualWidth { get; set; } = 420;
    public int ManualHeight { get; set; } = 40;
    public int AutoSizeOffset { get; set; } = 0;
    public int HorizontalAnchor { get; set; } = 0;
    public int WindowXOffset { get; set; } = 0;
    public int WindowYOffset { get; set; } = 0;
    public bool AlwaysOnTop { get; set; } = true;
    public bool BackgroundEnabled { get; set; } = true;
    public double BackgroundOpacity { get; set; } = 1.0;
    public bool BorderEnabled { get; set; } = false;
    public string BorderColor { get; set; } = "#404040";
    public bool TextShadow { get; set; } = true;

    // ==================== Animation ====================
    public int LyricTransition { get; set; } = 0;

    // ==================== Behavior ====================
    public bool PlayerAutoShow { get; set; } = true;
    public bool PlayerAutoHide { get; set; } = false;  // 默认不隐藏，方便调试
    public bool AutoStart { get; set; } = false;

    // ==================== Lyric Sync ====================
    /// <summary>歌词同步手动校准偏移（毫秒，正数=歌词提前显示，负数=歌词延后显示）。</summary>
    public int LyricOffsetMs { get; set; } = 0;

    // ==================== Cache ====================
    public bool EnablePlayerCache { get; set; } = true;
    public List<string> PlayerCacheFolders { get; set; } = new();
    public bool AutoDetectTaskbarSpace { get; set; } = true;
    public double TaskbarLeftOffset { get; set; } = 140;
    public List<string> MusicFolders { get; set; } = new();

    // ==================== Colors ====================
    public string TextColor { get; set; } = "#FFFFFF";
    public string BackgroundColor { get; set; } = "#101018";
    public string WaveColor { get; set; } = "#3BD0FF";
    public string FontFamily { get; set; } = "Novecento Wide";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }
}
