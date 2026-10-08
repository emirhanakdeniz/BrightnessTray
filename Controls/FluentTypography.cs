namespace BrightnessTray.Controls;

using System.Drawing;
using System.Windows.Forms;

public static class FluentTypography
{
    private static readonly string[] PreferredFamilies =
    [
        "Segoe UI Variable Text",
        "Segoe UI Variable",
        "Segoe UI"
    ];

    private static readonly string[] PreferredGlyphFamilies =
    [
        "Segoe Fluent Icons",
        "Segoe MDL2 Assets"
    ];

    private static readonly Lazy<string> ResolvedFamily = new(ResolveFontFamily);
    private static readonly Lazy<string> ResolvedGlyphFamily = new(ResolveGlyphFontFamily);

    public static string FontFamilyName => ResolvedFamily.Value;
    public static string GlyphFontFamilyName => ResolvedGlyphFamily.Value;

    private static string ResolveFontFamily()
    {
        foreach (var name in PreferredFamilies)
        {
            try
            {
                using var font = new Font(name, 9f);
                if (string.Equals(font.FontFamily.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
            catch
            {
                // Fall through to next font candidate
            }
        }

        return "Segoe UI";
    }

    private static string ResolveGlyphFontFamily()
    {
        foreach (var name in PreferredGlyphFamilies)
        {
            try
            {
                using var font = new Font(name, 9f);
                if (string.Equals(font.FontFamily.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
            catch
            {
                // Fall through to next font candidate
            }
        }

        return "Segoe MDL2 Assets";
    }

    public static Font CreateFont(float sizePt, FontStyle style = FontStyle.Regular)
    {
        return new Font(FontFamilyName, sizePt, style);
    }

    public static Font CreateGlyphFont(float sizePt)
    {
        return new Font(GlyphFontFamilyName, sizePt, FontStyle.Regular);
    }

    // Standardized Windows 11 Fluent typography sizes from Theme tokens
    public static Font CreateBodyFont(FontStyle style = FontStyle.Regular) => CreateFont(BrightnessTray.UI.Theme.FontSizeBody, style); // ~14px
    public static Font CreateSecondaryFont(FontStyle style = FontStyle.Regular) => CreateFont(BrightnessTray.UI.Theme.FontSizeSecondary, style);   // ~12px

    public static Color GetPrimaryTextColor(bool isDark) => BrightnessTray.UI.Theme.TextPrimary(isDark);
    public static Color GetSecondaryTextColor(bool isDark) => BrightnessTray.UI.Theme.TextSecondary(isDark);
    public static Color GetDisabledTextColor(bool isDark) => BrightnessTray.UI.Theme.TextDisabled(isDark);

    public static void DrawText(
        Graphics g,
        string text,
        Font font,
        Rectangle bounds,
        Color color,
        ContentAlignment align = ContentAlignment.MiddleLeft,
        bool autoEllipsis = false)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var brush = new SolidBrush(color);
        using var sf = new StringFormat(StringFormat.GenericTypographic);
        DrawText(g, text, font, bounds, brush, sf, align, autoEllipsis);
    }

    internal static void DrawText(Graphics g, string text, Font font, Rectangle bounds,
        SolidBrush brush, StringFormat sf, ContentAlignment align, bool autoEllipsis,
        System.Drawing.Text.TextRenderingHint renderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit)
    {
        if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
        sf.FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
        sf.Trimming = autoEllipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None;

        switch (align)
        {
            case ContentAlignment.TopLeft:
            case ContentAlignment.MiddleLeft:
            case ContentAlignment.BottomLeft:
                sf.Alignment = StringAlignment.Near;
                break;
            case ContentAlignment.TopCenter:
            case ContentAlignment.MiddleCenter:
            case ContentAlignment.BottomCenter:
                sf.Alignment = StringAlignment.Center;
                break;
            case ContentAlignment.TopRight:
            case ContentAlignment.MiddleRight:
            case ContentAlignment.BottomRight:
                sf.Alignment = StringAlignment.Far;
                break;
        }

        switch (align)
        {
            case ContentAlignment.TopLeft:
            case ContentAlignment.TopCenter:
            case ContentAlignment.TopRight:
                sf.LineAlignment = StringAlignment.Near;
                break;
            case ContentAlignment.MiddleLeft:
            case ContentAlignment.MiddleCenter:
            case ContentAlignment.MiddleRight:
                sf.LineAlignment = StringAlignment.Center;
                break;
            case ContentAlignment.BottomLeft:
            case ContentAlignment.BottomCenter:
            case ContentAlignment.BottomRight:
                sf.LineAlignment = StringAlignment.Far;
                break;
        }

        g.TextRenderingHint = renderingHint;
        var rectF = new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        g.DrawString(text, font, brush, rectF, sf);
    }
}
