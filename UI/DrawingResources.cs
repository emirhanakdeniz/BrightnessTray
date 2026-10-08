namespace BrightnessTray.UI;

using System.Drawing.Drawing2D;
using BrightnessTray.Controls;

// Each control owns its cache. Nothing here outlives the owning UI element.
internal sealed class DrawingResources : IDisposable
{
    private readonly Dictionary<int, SolidBrush> _brushes = new();
    private readonly Dictionary<(int Color, float Width), Pen> _pens = new();
    private readonly Dictionary<float, Font> _glyphFonts = new();
    private StringFormat? _textFormat;
    private GraphicsPath? _roundedPath;
    private int _dpi;

    public void EnsureDpi(int dpi)
    {
        if (_dpi == dpi) return;
        Clear();
        _dpi = dpi;
    }

    public SolidBrush Brush(Color color)
    {
        int key = color.ToArgb();
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidBrush(color);
            _brushes.Add(key, brush);
        }
        return brush;
    }

    public Pen Pen(Color color, float width)
    {
        var key = (color.ToArgb(), width);
        if (!_pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(color, width);
            _pens.Add(key, pen);
        }
        return pen;
    }

    public Font GlyphFont(float size)
    {
        if (!_glyphFonts.TryGetValue(size, out var font))
        {
            font = FluentTypography.CreateGlyphFont(size);
            _glyphFonts.Add(size, font);
        }
        return font;
    }

    public void DrawText(Graphics graphics, string text, Font font, Rectangle bounds,
        Color color, ContentAlignment align = ContentAlignment.MiddleLeft, bool autoEllipsis = false,
        System.Drawing.Text.TextRenderingHint renderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit)
    {
        _textFormat ??= new StringFormat(StringFormat.GenericTypographic);
        FluentTypography.DrawText(graphics, text, font, bounds, Brush(color), _textFormat, align, autoEllipsis, renderingHint);
    }

    // The path is borrowed until the next call; callers must not dispose it.
    public GraphicsPath RoundedRectangle(RectangleF rect, float radius)
    {
        _roundedPath ??= new GraphicsPath();
        _roundedPath.Reset();
        if (rect.Width <= 0 || rect.Height <= 0) return _roundedPath;
        float diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (diameter <= 0)
        {
            _roundedPath.AddRectangle(rect);
            return _roundedPath;
        }
        var arc = new RectangleF(rect.Location, new SizeF(diameter, diameter));
        _roundedPath.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        _roundedPath.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        _roundedPath.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        _roundedPath.AddArc(arc, 90, 90);
        _roundedPath.CloseFigure();
        return _roundedPath;
    }

    public void Clear()
    {
        foreach (var brush in _brushes.Values) brush.Dispose();
        foreach (var pen in _pens.Values) pen.Dispose();
        foreach (var font in _glyphFonts.Values) font.Dispose();
        _brushes.Clear();
        _pens.Clear();
        _glyphFonts.Clear();
        _textFormat?.Dispose();
        _textFormat = null;
        _roundedPath?.Dispose();
        _roundedPath = null;
    }

    public void Dispose() => Clear();
}
