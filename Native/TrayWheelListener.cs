namespace BrightnessTray.Native;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// NotifyIcon has no MouseWheel event. This event-driven hook only inspects wheel
// messages; unrelated input is always passed through, with no polling timer.
internal sealed class TrayWheelListener : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmMouseWheel = 0x020A;
    private readonly NotifyIcon _icon;
    private readonly Func<int, bool> _onWheel;
    private readonly Action _reset;
    private readonly HookProc _callback;
    private IntPtr _hook;
    private Rectangle _lastIconRect;

    internal TrayWheelListener(NotifyIcon icon, Func<int, bool> onWheel, Action reset)
    {
        _icon = icon;
        _onWheel = onWheel;
        _reset = reset;
        _callback = OnMouseInput;
        _hook = SetWindowsHookEx(WhMouseLl, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            Debug.WriteLine($"[TrayWheel] Unable to install input hook: {Marshal.GetLastWin32Error()}");
    }

    private IntPtr OnMouseInput(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _hook != IntPtr.Zero)
        {
            try
            {
                if (message == (IntPtr)WmMouseWheel)
                {
                    var input = Marshal.PtrToStructure<MouseInput>(data);
                    var point = new Point(input.Point.X, input.Point.Y);
                    // Query on each wheel message: Explorer can move/recreate the icon.
                    // Checking the actual window also excludes an app covering its bounds.
                    if (_icon.Visible && ShellInterop.TryGetNotifyIconRect(_icon, out var rect) &&
                        rect.Contains(point) && IsTrayWindow(input.Point))
                    {
                        _lastIconRect = rect;
                        int delta = unchecked((short)(input.MouseData >> 16));
                        if (_onWheel(delta)) return (IntPtr)1;
                    }
                    _reset();
                }
                else if (message == (IntPtr)0x0200 && !_lastIconRect.IsEmpty)
                {
                    // A cached rectangle is sufficient to reset partial deltas on exit;
                    // never query Explorer for ordinary mouse movements.
                    var point = Marshal.PtrToStructure<ShellInterop.POINT>(data);
                    if (!_lastIconRect.Contains(point.X, point.Y))
                    {
                        _lastIconRect = Rectangle.Empty;
                        _reset();
                    }
                }
            }
            catch (Exception ex)
            {
                // Never let a managed exception escape a native input callback.
                Debug.WriteLine($"[TrayWheel] Input handling failed: {ex.Message}");
                _reset();
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool IsTrayWindow(ShellInterop.POINT point)
    {
        IntPtr window = GetAncestor(WindowFromPoint(point), 2); // GA_ROOT
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public ShellInterop.POINT Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(ShellInterop.POINT point);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);
}
