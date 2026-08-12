using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using TaskbarLyrics.Helpers;
using TaskbarLyrics.Services;

namespace TaskbarLyrics;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private Window? _mainWindow;

    /// <summary>Pre-warmed WebView2 environment for faster overlay init.</summary>
    public static CoreWebView2Environment? WebView2Env { get; private set; }

    public static AppSettings Settings { get; } = AppSettings.Load();
    public static LyricsManager Lyrics { get; } = new()
    {
        MusicFolders = Settings.MusicFolders.Count > 0
            ? Settings.MusicFolders.ToArray()
            : LyricsManager.DefaultMusicFolders,
        EnableOnline = Settings.EnableOnline,
        PlayerCache = new PlayerLyricsCache
        {
            Enabled = Settings.EnablePlayerCache,
            CacheFolders = Settings.PlayerCacheFolders.Count > 0
                ? Settings.PlayerCacheFolders.ToArray()
                : PlayerLyricsCache.DefaultCacheFolders
        }
    };
    public static OverlayWindow Overlay { get; private set; } = null!;

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_SHOWNORMAL = 1;
    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Pre-warm WebView2 environment in background
        _ = Task.Run(async () =>
        {
            try
            {
                var opts = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--disable-gpu --disable-extensions"
                };
                WebView2Env = await CoreWebView2Environment.CreateAsync(null, null, opts);
            }
            catch { }
        });

        // Single instance
        _mutex = new Mutex(true, "TaskbarLyrics.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            var hwnd = FindWindow(null, "任务栏歌词 - 设置");
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            _mutex.Dispose();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 后台预热歌词/音频索引，减少首次切歌时的本地检索延迟
        Lyrics.WarmUp();

        Overlay = new OverlayWindow(Settings, Lyrics);
        Overlay.Show();

        _mainWindow = new MainWindow();
        _mainWindow.Closing += (_, _) => _mainWindow.Hide();

        SetupTray();
        SetupAutoStart();
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    private void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示/隐藏悬浮窗", null, (_, _) => ToggleOverlay());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _autoStartItem = new System.Windows.Forms.ToolStripMenuItem("开机自启")
        {
            Checked = Settings.AutoStart
        };
        _autoStartItem.Click += (_, _) => ToggleAutoStart();
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Current.Shutdown());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "任务栏歌词",
            Icon = CreateAppIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ToggleOverlay();
    }

    private void ToggleOverlay()
    {
        if (Overlay.IsVisible) Overlay.HideByUser();
        else Overlay.ShowByUser();
    }

    private void ShowSettings()
    {
        // Window may have been closed — WPF can't reopen closed windows
        if (_mainWindow == null || !_mainWindow.IsLoaded)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closing += (_, e) => { e.Cancel = true; _mainWindow.Hide(); };
        }
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private System.Windows.Forms.ToolStripMenuItem? _autoStartItem;

    private void ToggleAutoStart()
    {
        Settings.AutoStart = !Settings.AutoStart;
        Settings.Save();
        SetupAutoStart();
        // Update tray checkmark
        if (_autoStartItem != null) _autoStartItem.Checked = Settings.AutoStart;
    }

    private void SetupAutoStart()
    {
        var exePath = Environment.ProcessPath ?? "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            if (Settings.AutoStart && !string.IsNullOrEmpty(exePath))
                key?.SetValue("TaskbarLyrics.Light", '"' + exePath + '"');
            else
                key?.DeleteValue("TaskbarLyrics.Light", false);
        }
        catch { }
    }

    private static System.Drawing.Icon CreateAppIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.DeepSkyBlue, 1.6f);
            using var brush = new SolidBrush(Color.DeepSkyBlue);
            g.DrawEllipse(pen, 2, 9, 4, 4);
            g.DrawLine(pen, 6, 11, 6, 3);
            g.DrawLine(pen, 6, 3, 12, 1);
            g.DrawLine(pen, 12, 1, 12, 7);
            g.FillEllipse(brush, 10, 7, 3, 3);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
