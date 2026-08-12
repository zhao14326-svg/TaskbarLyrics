using System.Runtime.InteropServices;

namespace TaskbarLyrics.Helpers;

/// <summary>
/// Detects the empty/available space on the Windows taskbar
/// between the task list (pinned+running apps) and the notification tray area.
/// Supports Windows 10 and Windows 11 taskbar layouts.
/// </summary>
public static class TaskbarSpaceDetector
{
    public readonly record struct TaskbarRegion(
        RECT Taskbar,
        double EmptyLeft,
        double EmptyRight,
        bool LeftDetected,
        bool RightDetected
    );

    private const int Margin = 8; // px gap on each side

    public static TaskbarRegion Detect()
    {
        var tb = NativeMethods.GetTaskbarRect();
        if (tb == null)
            return new TaskbarRegion(default, 0, 1920, false, false);

        var hwnd = NativeMethods.GetTaskbarWnd();
        double defaultRight = tb.Value.Right - 320;
        double defaultLeft = tb.Value.Left;

        bool leftOk = false;
        bool rightOk = false;
        double left = defaultLeft;
        double right = defaultRight;

        if (hwnd != IntPtr.Zero)
        {
            // --- RIGHT boundary: left edge of tray/notification area ---
            var tray = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray == IntPtr.Zero)
            {
                // Win11: the tray area uses different class names
                var children = EnumerateChildren(hwnd);
                var trayCandidates = children
                    .Where(w =>
                    {
                        var cls = NativeMethods.GetWindowClassName(w);
                        return cls.Contains("Tray", StringComparison.OrdinalIgnoreCase)
                            || cls.Contains("Notify", StringComparison.OrdinalIgnoreCase)
                            || cls.Contains("SystemTray", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                if (trayCandidates.Count > 0)
                {
                    tray = trayCandidates
                        .OrderBy(w => NativeMethods.GetWindowRect(w).Left)
                        .First();
                }
            }
            if (tray != IntPtr.Zero)
            {
                var trayRect = NativeMethods.GetWindowRect(tray);
                right = trayRect.Left - Margin;
                rightOk = true;
            }

            // --- LEFT boundary: right edge of task list / start area ---
            var taskList = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "MSTaskSwWClass", null);
            if (taskList == IntPtr.Zero)
            {
                taskList = NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "MSTaskListWClass", null);
            }
            if (taskList != IntPtr.Zero)
            {
                var tlRect = NativeMethods.GetWindowRect(taskList);
                left = tlRect.Right + Margin;
                leftOk = true;
            }
            else
            {
                // Win11 fallback: approximate start area as taskbar height × 2.4
                double taskbarH = tb.Value.Bottom - tb.Value.Top;
                left = tb.Value.Left + taskbarH * 2.4;
            }
        }

        // Clamp to valid range
        left = Math.Clamp(left, tb.Value.Left, right - 80);
        right = Math.Clamp(right, left + 80, tb.Value.Right);

        return new TaskbarRegion(tb.Value, left, right, leftOk, rightOk);
    }

    private static List<IntPtr> EnumerateChildren(IntPtr parent)
    {
        var list = new List<IntPtr>();
        NativeMethods.EnumChildWindows(parent, (hwnd, _) =>
        {
            list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
