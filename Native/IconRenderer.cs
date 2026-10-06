namespace BrightnessTray.Native;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

public static class IconRenderer
{
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;

    private static readonly Lazy<FontFamily?> IconFontFamily = new(ResolveIconFontFamily);

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
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            var fontFamily = IconFontFamily.Value;
            if (fontFamily is not null)
            {
                // Optical glyph sizing centered in the square
                float fontSize = size * 0.72f;
                using var font = new Font(fontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(glyphColor);
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };

                g.DrawString("\uE706", font, brush, new RectangleF(0, 0, size, size), sf);
            }
            else
            {
                DrawFallbackSun(g, size, glyphColor);
            }
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

    private static void DrawFallbackSun(Graphics g, int size, Color color)
    {
        float cx = size / 2f;
        float cy = size / 2f;

        float rCircle = size * 0.20f;
        float rInner = size * 0.28f;
        float rOuter = size * 0.44f;
        float penWidth = Math.Max(1.2f, size * 0.08f);

        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, penWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        // Center circle
        g.FillEllipse(brush, cx - rCircle, cy - rCircle, rCircle * 2f, rCircle * 2f);

        // 8 radial rays
        for (int i = 0; i < 8; i++)
        {
            float angleRad = (float)(i * 45 * Math.PI / 180.0);
            float cos = (float)Math.Cos(angleRad);
            float sin = (float)Math.Sin(angleRad);

            float x1 = cx + (rInner * cos);
            float y1 = cy + (rInner * sin);
            float x2 = cx + (rOuter * cos);
            float y2 = cy + (rOuter * sin);

            g.DrawLine(pen, x1, y1, x2, y2);
        }
    }

    private static FontFamily? ResolveIconFontFamily()
    {
        try
        {
            var families = FontFamily.Families;
            foreach (var f in families)
            {
                if (string.Equals(f.Name, "Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }

            foreach (var f in families)
            {
                if (string.Equals(f.Name, "Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
        }
        catch
        {
            // Fail silently
        }

        return null;
    }

    public static void GenerateAppIconFile(string outputPath)
    {
        int[] sizes = [16, 24, 32, 48, 256];
        byte[][] pngData = new byte[sizes.Length][];

        var fontFamily = IconFontFamily.Value;

        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;

                // Windows 11 rounded tile background
                float cornerRadius = size * 0.22f;
                using var path = CreateRoundedRectPath(new RectangleF(0.5f, 0.5f, size - 1f, size - 1f), cornerRadius);

                using (var bgBrush = new LinearGradientBrush(
                    new PointF(0, 0), new PointF(0, size),
                    Color.FromArgb(44, 44, 44), Color.FromArgb(24, 24, 24)))
                {
                    g.FillPath(bgBrush, path);
                }

                using (var borderPen = new Pen(Color.FromArgb(70, 70, 70), Math.Max(1f, size * 0.03f)))
                {
                    g.DrawPath(borderPen, path);
                }

                // White brightness glyph in center
                if (fontFamily is not null)
                {
                    float fontSize = size * 0.54f;
                    using var font = new Font(fontFamily, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                    using var brush = new SolidBrush(Color.White);
                    using var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };

                    g.DrawString("\uE706", font, brush, new RectangleF(0, 0, size, size), sf);
                }
                else
                {
                    DrawFallbackSun(g, size, Color.White);
                }
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngData[i] = ms.ToArray();
        }

        // Build binary ICO file
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // ICONDIR
        bw.Write((short)0); // idReserved
        bw.Write((short)1); // idType = 1 (icon)
        bw.Write((short)sizes.Length); // idCount

        int offset = 6 + (16 * sizes.Length);

        // ICONDIRENTRY list
        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            bw.Write((byte)(s >= 256 ? 0 : s)); // bWidth
            bw.Write((byte)(s >= 256 ? 0 : s)); // bHeight
            bw.Write((byte)0); // bColorCount
            bw.Write((byte)0); // bReserved
            bw.Write((short)1); // wPlanes
            bw.Write((short)32); // wBitCount
            bw.Write(pngData[i].Length); // dwBytesInRes
            bw.Write(offset); // dwImageOffset
            offset += pngData[i].Length;
        }

        // PNG image payloads
        for (int i = 0; i < sizes.Length; i++)
        {
            bw.Write(pngData[i]);
        }
    }

    private static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2f;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
