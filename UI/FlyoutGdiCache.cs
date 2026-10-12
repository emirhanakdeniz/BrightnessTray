namespace BrightnessTray.UI;

using System.Drawing;
using BrightnessTray.Controls;

public sealed class FlyoutGdiCache : IDisposable
{
    public Font BodyBoldFont { get; }
    public Font BodyFont { get; }
    public Font MonitorNameFont { get; }
    public Font SecondaryFont { get; }
    public Font GlyphLargeFont { get; }

    private bool _disposed;

    public FlyoutGdiCache()
    {
        BodyBoldFont = FluentTypography.CreateBodyFont(FontStyle.Bold);
        BodyFont = FluentTypography.CreateBodyFont();
        MonitorNameFont = FluentTypography.CreateSemiboldBodyFont();
        SecondaryFont = FluentTypography.CreateSecondaryFont();
        GlyphLargeFont = FluentTypography.CreateGlyphFont(Theme.FontSizeGlyphLarge);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            BodyBoldFont.Dispose();
            BodyFont.Dispose();
            MonitorNameFont.Dispose();
            SecondaryFont.Dispose();
            GlyphLargeFont.Dispose();
        }
    }
}
