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

        public Rectangle ToRectangle() =>
            Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

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
}
