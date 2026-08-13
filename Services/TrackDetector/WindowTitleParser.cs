using System.Diagnostics;
using System.Text.RegularExpressions;
using TaskbarLyrics.Helpers;

namespace TaskbarLyrics.Services;

/// <summary>
/// 通用窗口标题扫描：从所有进程窗口标题中识别 "歌曲 - 歌手" 格式。
/// 兼容网易云、QQ音乐、酷狗等；带 500ms 缓存 + 后台执行，UI 线程永不阻塞。
/// </summary>
public class WindowTitleParser
{
    // 已知的音乐播放器进程名（不含 .exe）
    private static readonly HashSet<string> KnownPlayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "cloudmusic", "netease-cloud-music", "neteasecloudmusic",  // 网易云
        "qqmusic", "qqmusicplayer",                                 // QQ音乐
        "kugou", "kugoumusic", "kgmusic",                          // 酷狗
        "kwmusic", "kwm",                                           // 酷我
        "foobar2000", "musicbee", "aimp",                          // 其他
        "wmplayer", "spotify",                                      // WMP, Spotify
        "thunder", "xmp",                                           // 迅雷看看等
    };

    private MediaTrack? _scanCache;
    private DateTime _scanCacheTime = DateTime.MinValue;
    private readonly object _scanLock = new();
    private volatile bool _scanRunning;

    /// <summary>
    /// 窗口标题扫描（500ms 缓存 + 后台执行：UI 线程永不阻塞）。
    /// 标题读取全部走 SendMessageTimeout(150ms)：即使某个窗口消息泵卡住，
    /// 单个窗口最多等待 150ms 且发生在后台线程，不影响 UI 线程与 250ms 定时器。
    /// </summary>
    public MediaTrack? ScanWindowTitles()
    {
        lock (_scanLock)
        {
            var now = DateTime.UtcNow;
            // 缓存有效期内直接返回（无需启动扫描）
            if (_scanCacheTime != DateTime.MinValue && (now - _scanCacheTime).TotalMilliseconds < 500)
                return _scanCache;
            // 后台扫描进行中：返回上次结果（最多滞后 ~500ms+扫描时间，检测可接受）
            if (_scanRunning) return _scanCache;
            _scanRunning = true;
            _scanCacheTime = now;
            _ = Task.Run(() =>
            {
                try { _scanCache = ScanAllWindows(); }
                finally { _scanRunning = false; }
            });
            return _scanCache;
        }
    }

    /// <summary>扫描所有进程，从窗口标题中识别 "歌曲 - 歌手" 格式。</summary>
    private MediaTrack? ScanAllWindows()
    {
        try
        {
            var allProcs = Process.GetProcesses();
            foreach (var p in allProcs)
            {
                try
                {
                    // 无主窗口的后台进程无需读标题——避免向大量窗口发送 WM_GETTEXT
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    // 带超时读取（150ms）：即使目标窗口消息泵卡住也不会阻塞扫描线程
                    var title = NativeMethods.GetWindowTextTimeout(p.MainWindowHandle, 150);
                    if (string.IsNullOrWhiteSpace(title) || title.Length < 2) continue;

                    // 部分播放器暂停时会在标题标注“已暂停/暂停中/[Paused]”等——识别并剥离标记
                    bool paused = false;
                    if (title.Contains("已暂停") || title.Contains("暂停中") || title.Contains("（已暂停）") || title.Contains("(已暂停)") ||
                        title.IndexOf("[Paused]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        title.IndexOf(" Paused", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        paused = true;
                        title = Regex.Replace(title,
                            "（已暂停）|\\(已暂停\\)|已暂停|暂停中|\\[Paused\\]|\\s?Paused",
                            "", RegexOptions.IgnoreCase)
                            .Trim().TrimEnd('-', '–', '—').TrimStart('-', '–', '—').Trim();
                        if (title.Length < 2) continue;
                    }

                    // 尝试从窗口标题解析 "歌曲 - 歌手" 格式
                    var (song, artist) = ParseTitle(title);
                    if (song == null) continue;

                    var procName = p.ProcessName;

                    // 过滤明显噪音（如浏览器标签页）
                    if (procName is "msedge" or "chrome" or "firefox" or "brave") continue;
                    if (song.Contains("Microsoft") || song.Contains("Windows")) continue;

                    // 只信任已知音乐播放器进程，或标题含明显音乐标记的窗口。
                    // 不采用“非已知窗口标题看着像歌曲”的兜底：ccswitch 等桌面工具的
                    // 窗口标题（如 "com.ccswitch.desktop - siw"）会被误判成歌曲，
                    // 触发 OverlayWindow 误判换歌，清空歌词/封面。
                    bool isKnown = KnownPlayers.Contains(procName)
                        || title.Contains("音乐") || title.Contains("Music")
                        || title.Contains("正在播放") || title.Contains("播放中")
                        || title.Contains("Now Playing") || title.Contains("现在播放");

                    if (!isKnown) continue;

                    return new MediaTrack(song, artist ?? "", "",
                        procName, paused ? "Paused" : "Playing", TimeSpan.Zero, TimeSpan.Zero, null);
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>从窗口标题解析 "歌曲名 - 歌手名" 格式（通用）。</summary>
    public (string? song, string? artist) ParseTitle(string title)
    {
        // 匹配 "A - B" 格式（至少 2 个字符的歌名）
        var m = Regex.Match(title, @"^(.{2,}?)\s*[-–—]\s*(.{2,}?)(?:\s*[-–—]\s*.+)?$");
        if (!m.Success) return (null, null);

        var part1 = m.Groups[1].Value.Trim();
        var part2 = m.Groups[2].Value.Trim();

        // 过滤掉非歌曲标题
        if (part1.Contains("桌面歌词") || part1.Contains("歌词") && part1.Length < 8) return (null, null);
        if (part2.Contains("桌面歌词") || part2.Contains("歌词") && part2.Length < 8) return (null, null);

        // 主流播放器窗口标题统一为 "歌名 - 歌手"（网易云/QQ音乐/酷狗/Spotify 等）。
        // 不做“按长度猜反转”：那会把 "Everybody - Ingrid Michaelson" 误判成
        // “歌名=Ingrid Michaelson”，与 SMTC 曲目不一致，触发误判换歌清空歌词。
        return (part1, part2);
    }
}
