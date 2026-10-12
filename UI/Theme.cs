namespace BrightnessTray.UI;

using System.Drawing;

public static class Theme
{
    // --- 4px Grid Spacing Scale ---
    public const int BaseSpace4 = 4;
    public const int BaseSpace8 = 8;
    public const int BaseSpace12 = 12;

    // --- Window Positioning Tokens ---
    public const int FlyoutEdgeGap = 12;
    public const int FlyoutWidth = 312;
    public const int FlyoutVerticalPadding = 4;
    public const int MonitorRowHeight = 72;
    public const int CollapsedMonitorRowHeight = 46;
    public const int MonitorRowHorizontalPadding = 18;

    // --- Radii ---
    public const int BaseRadius4 = 4;

    // --- DPI-aware Sizing Helpers ---
    public static int Scale(int pixels, float dpiScale) => (int)Math.Round(pixels * dpiScale);

    public static int Space4(float dpiScale) => Scale(BaseSpace4, dpiScale);
    public static int Space8(float dpiScale) => Scale(BaseSpace8, dpiScale);
    public static int Space12(float dpiScale) => Scale(BaseSpace12, dpiScale);
    public static int FlyoutGap(float dpiScale) => Scale(FlyoutEdgeGap, dpiScale);

    public static int Radius4(float dpiScale) => Math.Max(2, Scale(BaseRadius4, dpiScale));

    // --- Typography Sizes (pt) ---
    public const float FontSizeSecondary = 9.0f;  // ~12px
    public const float FontSizeBody = 10.5f;      // ~14px
    public const float FontSizeGlyphSmall = 8.5f;
    public const float FontSizeGlyphMedium = 9.5f;
    public const float FontSizeGlyphLarge = 24.0f;

    // --- Theme Colors ---
    public static Color TextPrimary(bool isDark) =>
        isDark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(26, 26, 26);

    public static Color TextSecondary(bool isDark) =>
        isDark ? Color.FromArgb(158, 158, 158) : Color.FromArgb(94, 94, 94);

    public static Color TextDisabled(bool isDark) =>
        isDark ? Color.FromArgb(85, 85, 85) : Color.FromArgb(176, 176, 176);

    public static Color BackgroundBase(bool isDark) =>
        isDark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 246, 250);

    public static Color FieldBackground(bool isDark) =>
        isDark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(229, 229, 229);

    public static Color MenuBackground(bool isDark) =>
        isDark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);

    public static Color MenuItemOverlay(bool isDark, bool pressed) =>
        isDark ? Color.FromArgb(pressed ? 32 : 22, 255, 255, 255)
            : Color.FromArgb(pressed ? 22 : 14, 0, 0, 0);

    public static Color TrackUnfilled(bool isDark) =>
        isDark ? Color.FromArgb(155, 155, 155) : Color.FromArgb(151, 151, 151);

    public static Color SliderFill(bool isDark) =>
        IsHighContrast ? SystemColors.Highlight
            : isDark ? Color.FromArgb(179, 191, 181) : Color.FromArgb(82, 94, 85);

    public static Color FlyoutText(bool isDark) =>
        IsHighContrast ? SystemColors.WindowText
            : isDark ? Color.FromArgb(242, 245, 250) : Color.FromArgb(26, 26, 26);

    public static Color FlyoutIcon(bool isDark) =>
        IsHighContrast ? SystemColors.WindowText
            : isDark ? Color.FromArgb(190, 190, 190) : Color.FromArgb(78, 94, 114);

    public static Color FlyoutDivider(bool isDark) =>
        IsHighContrast ? SystemColors.WindowText
            : isDark ? Color.FromArgb(49, 49, 49) : Color.FromArgb(215, 224, 234);

    public static Color SliderThumb(bool isDark) =>
        IsHighContrast ? SystemColors.Window : isDark ? Color.FromArgb(72, 77, 73) : Color.FromArgb(210, 215, 211);

    private static bool IsHighContrast => System.Windows.Forms.SystemInformation.HighContrast;

    public static Color BorderSubtle(bool isDark) =>
        isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(219, 219, 219);

}
