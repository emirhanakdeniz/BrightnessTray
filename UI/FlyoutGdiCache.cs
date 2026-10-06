namespace BrightnessTray.UI;

using System.Drawing;
using BrightnessTray.Controls;

public sealed class FlyoutGdiCache : IDisposable
{
    public Font BodyFont { get; }
    public Font BodyBoldFont { get; }
    public Font SecondaryFont { get; }
    public Font BadgeFont { get; }
    public Font GlyphSmallFont { get; }
    public Font GlyphMediumFont { get; }
    public Font GlyphLargeFont { get; }

    private bool _disposed;

    public FlyoutGdiCache()
    {
        BodyFont = FluentTypography.CreateBodyFont();
        BodyBoldFont = FluentTypography.CreateBodyFont(FontStyle.Bold);
        SecondaryFont = FluentTypography.CreateSecondaryFont();
        BadgeFont = FluentTypography.CreateBadgeFont();
        GlyphSmallFont = FluentTypography.CreateGlyphFont(Theme.FontSizeGlyphSmall);
        GlyphMediumFont = FluentTypography.CreateGlyphFont(Theme.FontSizeGlyphMedium);
        GlyphLargeFont = FluentTypography.CreateGlyphFont(Theme.FontSizeGlyphLarge);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            BodyFont.Dispose();
            BodyBoldFont.Dispose();
            SecondaryFont.Dispose();
            BadgeFont.Dispose();
            GlyphSmallFont.Dispose();
            GlyphMediumFont.Dispose();
            GlyphLargeFont.Dispose();
        }
    }
}
