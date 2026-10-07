namespace BrightnessTray.Native;

using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public static class ShellInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RECT(Rectangle r)
        {
            Left = r.Left;
            Top = r.Top;
            Right = r.Right;
            Bottom = r.Bottom;
        }

        public Rectangle ToRectangle() =>
            Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    public enum MONITOR_DPI_TYPE
    {
        MDT_EFFECTIVE_DPI = 0,
        MDT_ANGULAR_DPI = 1,
        MDT_RAW_DPI = 2,
        MDT_DEFAULT = MDT_EFFECTIVE_DPI
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll", SetLastError = true)]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int Shell_NotifyIconGetRect(
        [In] ref NOTIFYICONIDENTIFIER identifier,
        [Out] out RECT iconLocation);

    public static bool TryGetNotifyIconRect(NotifyIcon notifyIcon, out Rectangle rect)
    {
        rect = Rectangle.Empty;

        try
        {
            var windowField = typeof(NotifyIcon).GetField("window", BindingFlags.NonPublic | BindingFlags.Instance);
            var idField = typeof(NotifyIcon).GetField("id", BindingFlags.NonPublic | BindingFlags.Instance);

            if (windowField?.GetValue(notifyIcon) is NativeWindow window &&
                idField?.GetValue(notifyIcon) is int id &&
                window.Handle != IntPtr.Zero)
            {
                var identifier = new NOTIFYICONIDENTIFIER
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                    hWnd = window.Handle,
                    uID = (uint)id,
                    guidItem = Guid.Empty
                };

                int hr = Shell_NotifyIconGetRect(ref identifier, out var nativeRect);
                if (hr == 0 && nativeRect.Right > nativeRect.Left && nativeRect.Bottom > nativeRect.Top)
                {
                    rect = nativeRect.ToRectangle();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShellInterop] Failed to query Shell_NotifyIconGetRect: {ex.Message}");
        }

        return false;
    }

    public static bool TryGetTrayMonitorInfo(
        Rectangle? iconRect,
        Point cursorFallback,
        out Rectangle workArea,
        out Rectangle monitorBounds,
        out float dpiScale)
    {
        workArea = Rectangle.Empty;
        monitorBounds = Rectangle.Empty;
        dpiScale = 1.0f;
        IntPtr hMonitor = IntPtr.Zero;

        try
        {
            if (iconRect.HasValue && !iconRect.Value.IsEmpty)
            {
                var rect = new RECT(iconRect.Value);
                hMonitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
            }

            if (hMonitor == IntPtr.Zero)
            {
                var pt = new POINT(cursorFallback.X, cursorFallback.Y);
                hMonitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            }

            if (hMonitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    workArea = mi.rcWork.ToRectangle();
                    monitorBounds = mi.rcMonitor.ToRectangle();

                    try
                    {
                        int hr = GetDpiForMonitor(hMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _);
                        if (hr == 0 && dpiX > 0)
                        {
                            dpiScale = dpiX / 96f;
                        }
                    }
                    catch
                    {
                        // Fall back to system DPI if GetDpiForMonitor is unavailable
                    }

                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShellInterop] Failed to get monitor info: {ex.Message}");
        }

        var screen = Screen.FromPoint(cursorFallback);
        workArea = screen.WorkingArea;
        monitorBounds = screen.Bounds;
        dpiScale = 1.0f;
        return false;
    }
}
