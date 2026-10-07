namespace BrightnessTray.UI;

using System.Drawing;
using System.Drawing.Drawing2D;

public static class Theme
{
    // --- 4px Grid Spacing Scale ---
    public const int BaseSpace4 = 4;
    public const int BaseSpace8 = 8;
    public const int BaseSpace12 = 12;
    public const int BaseSpace16 = 16;
    public const int BaseSpace24 = 24;

    // --- Window Positioning Tokens ---
    public const int FlyoutEdgeGap = 12;

    // --- Radii ---
    public const int BaseRadius4 = 4;
    public const int BaseRadius8 = 8;

    // --- DPI-aware Sizing Helpers ---
    public static int Scale(int pixels, float dpiScale) => (int)Math.Round(pixels * dpiScale);

    public static int Space4(float dpiScale) => Scale(BaseSpace4, dpiScale);
    public static int Space8(float dpiScale) => Scale(BaseSpace8, dpiScale);
    public static int Space12(float dpiScale) => Scale(BaseSpace12, dpiScale);
    public static int Space16(float dpiScale) => Scale(BaseSpace16, dpiScale);
    public static int Space24(float dpiScale) => Scale(BaseSpace24, dpiScale);
    public static int FlyoutGap(float dpiScale) => Scale(FlyoutEdgeGap, dpiScale);

    public static int Radius4(float dpiScale) => Math.Max(2, Scale(BaseRadius4, dpiScale));
    public static int Radius8(float dpiScale) => Math.Max(4, Scale(BaseRadius8, dpiScale));

    // --- Typography Sizes (pt) ---
    public const float FontSizeSecondary = 9.0f;  // ~12px
    public const float FontSizeBody = 10.5f;      // ~14px
    public const float FontSizeHeader = 15.0f;    // ~20px
    public const float FontSizeGlyphSmall = 8.5f;
    public const float FontSizeGlyphMedium = 9.5f;
    public const float FontSizeGlyphLarge = 24.0f;

    // --- Theme Colors ---
    public static Color TextPrimary(bool isDark) =>
        isDark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(26, 26, 26);

    public static Color TextSecondary(bool isDark) =>
        isDark ? Color.FromArgb(158, 158, 158) : Color.FromArgb(94, 94, 94);

    public static Color TextLowContrast(bool isDark) =>
        isDark ? Color.FromArgb(112, 112, 112) : Color.FromArgb(138, 138, 138);

    public static Color TextDisabled(bool isDark) =>
        isDark ? Color.FromArgb(85, 85, 85) : Color.FromArgb(176, 176, 176);

    public static Color BackgroundBase(bool isDark) =>
        isDark ? Color.FromArgb(32, 32, 33) : Color.FromArgb(243, 243, 243);

    public static Color FieldBackground(bool isDark) =>
        isDark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(229, 229, 229);

    public static Color TrackUnfilled(bool isDark) =>
        isDark ? Color.FromArgb(77, 77, 77) : Color.FromArgb(220, 222, 224);

    public static Color HoverOverlay(bool isDark) =>
        isDark ? Color.FromArgb(10, 255, 255, 255) : Color.FromArgb(8, 0, 0, 0);

    public static Color PressedOverlay(bool isDark) =>
        isDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(16, 0, 0, 0);

    public static Color BorderSubtle(bool isDark) =>
        isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(219, 219, 219);

    // --- Accent Variants ---
    public static Color AccentHover(Color accent, bool isDark)
    {
        return ShiftLuminance(accent, isDark ? 0.08f : -0.08f);
    }

    public static Color AccentPressed(Color accent, bool isDark)
    {
        return ShiftLuminance(accent, isDark ? -0.08f : 0.08f);
    }

    private static Color ShiftLuminance(Color color, float factor)
    {
        float r = color.R / 255f;
        float g = color.G / 255f;
        float b = color.B / 255f;

        if (factor > 0)
        {
            r += (1f - r) * factor;
            g += (1f - g) * factor;
            b += (1f - b) * factor;
        }
        else
        {
            float f = 1f + factor;
            r *= f;
            g *= f;
            b *= f;
        }

        return Color.FromArgb(
            color.A,
            Math.Clamp((int)Math.Round(r * 255f), 0, 255),
            Math.Clamp((int)Math.Round(g * 255f), 0, 255),
            Math.Clamp((int)Math.Round(b * 255f), 0, 255));
    }

    // --- Graphics Helpers ---
    public static GraphicsPath CreateRoundedRectanglePath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2f;
        var arc = new RectangleF(rect.Location, new SizeF(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
