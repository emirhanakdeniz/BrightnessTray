namespace BrightnessTray.UI;

using System.Drawing;
using BrightnessTray.Controls;

public sealed class FlyoutGdiCache : IDisposable
{
    public Font BodyBoldFont { get; }
    public Font SecondaryFont { get; }
    public Font GlyphSmallFont { get; }
    public Font GlyphMediumFont { get; }
    public Font GlyphLargeFont { get; }

    private bool _disposed;

    public FlyoutGdiCache()
    {
        BodyBoldFont = FluentTypography.CreateBodyFont(FontStyle.Bold);
        SecondaryFont = FluentTypography.CreateSecondaryFont();
        GlyphSmallFont = FluentTypography.CreateGlyphFont(Theme.FontSizeGlyphSmall);
        GlyphMediumFont = FluentTypography.CreateGlyphFont(Theme.FontSizeGlyphMedium);
        GlyphLargeFont = FluentTypography.CreateGlyphFont(Theme.FontSizeGlyphLarge);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            BodyBoldFont.Dispose();
            SecondaryFont.Dispose();
            GlyphSmallFont.Dispose();
            GlyphMediumFont.Dispose();
            GlyphLargeFont.Dispose();
        }
    }
}
