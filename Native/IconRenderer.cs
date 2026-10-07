namespace BrightnessTray.Native;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

public static class IconRenderer
{
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public static int GetTaskbarDpi()
    {
        try
        {
            uint dpi = GetDpiForSystem();
            if (dpi > 0)
            {
                return (int)dpi;
            }
        }
        catch
        {
            // Fail silently
        }

        return 96;
    }

    public static Icon CreateBrightnessIcon(bool lightTaskbar, int dpi)
    {
        int size = GetSystemMetricsForDpi(SM_CXSMICON, (uint)dpi);
        if (size <= 0)
        {
            size = (int)Math.Round(16.0 * dpi / 96.0);
        }

        // Near-black for light taskbar, pure white for dark taskbar
        var glyphColor = lightTaskbar
            ? Color.FromArgb(0x1B, 0x1B, 0x1B)
            : Color.FromArgb(255, 255, 255);

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            DrawFluentSun(g, size, glyphColor);
        }

        // Convert to Icon via GetHicon, clone managed Icon, then free native HICON to prevent leaks
        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public static bool IsSystemTaskbarLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            if (value is int intVal)
            {
                return intVal == 1;
            }
        }
        catch
        {
            // Fail silently
        }

        return false; // Default to dark taskbar on Windows 11
    }

    private static void DrawFluentSun(Graphics g, int size, Color color)
    {
        float cx = size / 2f;
        float cy = size / 2f;
        float scale = size / 16f;

        // Proportions based on Microsoft Fluent UI System Icons (Weather Sunny / Brightness filled)
        float rCircle = 3.0f * scale;
        float rayInner = 4.8f * scale;
        float rayOuter = 6.9f * scale;
        float rayWidth = Math.Max(1.25f, 1.25f * scale);

        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, rayWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        // Solid core sun circle for crisp contrast and visibility against any taskbar
        g.FillEllipse(brush, cx - rCircle, cy - rCircle, rCircle * 2f, rCircle * 2f);

        // 8 symmetrical radial rays with rounded caps
        for (int i = 0; i < 8; i++)
        {
            float angleRad = (float)(i * 45.0 * Math.PI / 180.0);
            float cos = (float)Math.Cos(angleRad);
            float sin = (float)Math.Sin(angleRad);

            float x1 = cx + (rayInner * cos);
            float y1 = cy + (rayInner * sin);
            float x2 = cx + (rayOuter * cos);
            float y2 = cy + (rayOuter * sin);

            g.DrawLine(pen, x1, y1, x2, y2);
        }
    }
}

