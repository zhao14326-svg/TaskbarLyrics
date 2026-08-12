using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarLyrics.Helpers;

/// <summary>Windows API 封装：窗口穿透、任务栏定位、子窗口枚举、窗口区域</summary>
public static class NativeMethods
{
    // 窗口属性偏移
    public const int GWL_EXSTYLE = -20;
    public const int GWL_HWNDPARENT = -8;
    // 不显示在任务栏/Alt-Tab
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    // 鼠标事件穿透
    public const long WS_EX_TRANSPARENT = 0x00000020;
    // 不接收键盘焦点
    public const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    // SetWindowPos 常量：插入位置与标志
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern uint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    private const uint ABM_GETTASKBARPOS = 0x00000005;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    // ==================== Taskbar / Window enumeration ====================

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // ==================== Window Region (rounded corners) ====================

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    // ==================== Acrylic / Frosted Glass ====================

    // Windows 11: DWMWA_SYSTEMBACKDROP_TYPE
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_TRANSIENTWINDOW = 3; // 亚克力(毛玻璃)

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // Windows 10: SetWindowCompositionAttribute (ACCENT_ENABLE_ACRYLICBLURBEHIND)
    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor; // ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>为窗口启用系统级毛玻璃背景（Win11 亚克力 → Win10 亚克力）。失败时静默返回。</summary>
    public static void EnableAcrylic(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        // Windows 11: 系统后备背景类型 = 亚克力
        try
        {
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                return; // 成功
        }
        catch { }

        // Windows 10: 亚克力模糊
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = unchecked((int)0xCC101018) // ABGR: 深色半透明底色
            };
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>()),
                SizeOfData = Marshal.SizeOf<AccentPolicy>()
            };
            try
            {
                Marshal.StructureToPtr(accent, data.Data, false);
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(data.Data); }
        }
        catch { }
    }

    // ==================== Window text (timeout-safe) ====================

    public const uint WM_GETTEXT = 0x000D;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, [Out] StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    /// <summary>
    /// 带超时读取窗口标题文本。目标窗口消息泵卡住时最多等待 timeoutMs，不会像
    /// Process.MainWindowTitle 那样无限阻塞调用线程（这是 UI 线程被拖垮的根因）。
    /// </summary>
    public static string GetWindowTextTimeout(IntPtr hwnd, uint timeoutMs = 150)
    {
        if (hwnd == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(512);
            var ok = SendMessageTimeout(hwnd, WM_GETTEXT, new IntPtr(sb.Capacity), sb,
                SMTO_ABORTIFHUNG, timeoutMs, out _);
            if (ok == IntPtr.Zero) return ""; // 失败或目标窗口卡住
            return sb.ToString();
        }
        catch { return ""; }
    }

    // ==================== Convenience methods ====================

    /// <summary>获取任务栏矩形（屏幕坐标）。失败时返回 null。</summary>
    public static RECT? GetTaskbarRect()
    {
        try
        {
            var data = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>()
            };
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) != 0)
                return data.rc;
        }
        catch { }
        return null;
    }

    /// <summary>获取任务栏窗口句柄。</summary>
    public static IntPtr GetTaskbarWnd()
    {
        return FindWindow("Shell_TrayWnd", null);
    }

    /// <summary>获取窗口的类名。</summary>
    public static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>获取窗口矩形（屏幕坐标）。</summary>
    public static RECT GetWindowRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return r;
    }

    /// <summary>让窗口点击穿透且不抢键盘焦点（保留 Z-order，适合任务栏悬浮层）</summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        try
        {
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            style = (int)(style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style);
        }
        catch { }
    }

    /// <summary>
    /// Recursively make all child windows (including WebView2's browser sub-window) click-through.
    /// Call this AFTER WebView2 has fully initialized.
    /// </summary>
    public static void MakeChildrenClickThrough(IntPtr parentHwnd)
    {
        try
        {
            EnumChildWindows(parentHwnd, (hwnd, _) =>
            {
                try
                {
                    int style = GetWindowLong(hwnd, GWL_EXSTYLE);
                    style = (int)(style | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
                    SetWindowLong(hwnd, GWL_EXSTYLE, style);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
}
