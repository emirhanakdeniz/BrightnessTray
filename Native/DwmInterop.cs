namespace BrightnessTray.Native;

using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

public enum BackdropKind
{
    Mica,
    MicaAlt,
    Acrylic,
    None
}

public static class DwmInterop
{
    public enum DWMWINDOWATTRIBUTE
    {
        DWMWA_USE_IMMERSIVE_DARK_MODE = 20,
        DWMWA_WINDOW_CORNER_PREFERENCE = 33,
        DWMWA_BORDER_COLOR = 34,
        DWMWA_SYSTEMBACKDROP_TYPE = 38
    }

    public enum DWM_WINDOW_CORNER_PREFERENCE
    {
        DWMWCP_DEFAULT = 0,
        DWMWCP_DONOTROUND = 1,
        DWMWCP_ROUND = 2,
        DWMWCP_ROUNDSMALL = 3
    }

    public enum DWM_SYSTEMBACKDROP_TYPE
    {
        DWMSBT_AUTO = 0,
        DWMSBT_NONE = 1,
        DWMSBT_MAINWINDOW = 2,       // Mica
        DWMSBT_TRANSIENTWINDOW = 3,  // Acrylic
        DWMSBT_TABBEDWINDOW = 4      // Mica Alt
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;

        public MARGINS(int left, int right, int top, int bottom)
        {
            cxLeftWidth = left;
            cxRightWidth = right;
            cyTopHeight = top;
            cyBottomHeight = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OSVERSIONINFOEX
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmExtendFrameIntoClientArea(
        IntPtr hWnd,
        ref MARGINS pMarInset);

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int RtlGetVersion(ref OSVERSIONINFOEX lpVersionInformation);

    public static int GetOsBuildNumber()
    {
        try
        {
            var osvi = new OSVERSIONINFOEX();
            osvi.dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEX>();
            if (RtlGetVersion(ref osvi) == 0)
            {
                return osvi.dwBuildNumber;
            }
        }
        catch
        {
            // Fail silently
        }

        return Environment.OSVersion.Version.Build;
    }

    public static bool IsTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("EnableTransparency");
            if (val is int intVal)
            {
                return intVal == 1;
            }
        }
        catch
        {
            // Fail silently
        }

        return true;
    }

    public static bool IsHighContrast() => SystemInformation.HighContrast;

    public static bool IsMicaSupported() =>
        GetOsBuildNumber() >= 22621 && !IsHighContrast() && IsTransparencyEnabled();

    public static bool ApplyBackdrop(IntPtr hWnd, BackdropKind backdrop, bool isDarkMode)
    {
        if (!IsMicaSupported())
        {
            Debug.WriteLine($"[DwmInterop] Mica is not supported on Windows build {GetOsBuildNumber()} (< 22621). Using solid background fallback.");
            return false;
        }

        try
        {
            // 1. DWMWA_USE_IMMERSIVE_DARK_MODE (20): follow system app theme
            int darkMode = isDarkMode ? 1 : 0;
            DwmSetWindowAttribute(hWnd, (int)DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            // 2. DWMWA_WINDOW_CORNER_PREFERENCE (33): rounded corners
            int cornerPref = (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;
            DwmSetWindowAttribute(hWnd, (int)DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

            // 3. DWMWA_BORDER_COLOR (34): subtle theme-aware border
            int borderColor = isDarkMode
                ? (0x3C << 16) | (0x3C << 8) | 0x3C   // #3C3C3C dark border
                : (0xDB << 16) | (0xDB << 8) | 0xDB;  // #DBDBDB light border
            DwmSetWindowAttribute(hWnd, (int)DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

            // 4. DwmExtendFrameIntoClientArea with MARGINS(-1,-1,-1,-1) for whole client area
            var margins = new MARGINS(-1, -1, -1, -1);
            DwmExtendFrameIntoClientArea(hWnd, ref margins);

            // 5. DWMWA_SYSTEMBACKDROP_TYPE (38)
            int backdropType = backdrop switch
            {
                BackdropKind.Mica => (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW,
                BackdropKind.MicaAlt => (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_TABBEDWINDOW,
                BackdropKind.Acrylic => (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_TRANSIENTWINDOW,
                BackdropKind.None => (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_NONE,
                _ => (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW
            };
            DwmSetWindowAttribute(hWnd, (int)DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DwmInterop] Failed to apply DWM backdrop: {ex.Message}");
            return false;
        }
    }

    public static bool IsAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int intVal)
            {
                return intVal == 1;
            }
        }
        catch
        {
            // Fail silently
        }

        return false; // Default to dark app theme
    }

    public static Color GetAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            var value = key?.GetValue("AccentColor");
            if (value != null)
            {
                uint dword = Convert.ToUInt32(value);
                byte r = (byte)(dword & 0xFF);
                byte g = (byte)((dword >> 8) & 0xFF);
                byte b = (byte)((dword >> 16) & 0xFF);
                byte a = (byte)((dword >> 24) & 0xFF);
                if (a == 0) a = 255;
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch
        {
            // Fail silently
        }

        // Fallback: #0067C0
        return Color.FromArgb(0x00, 0x67, 0xC0);
    }
}
