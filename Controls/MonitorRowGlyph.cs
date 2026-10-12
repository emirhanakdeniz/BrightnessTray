namespace BrightnessTray.Controls;

using System.Drawing.Drawing2D;
using BrightnessTray.UI;

internal enum MonitorRowGlyphKind
{
    Monitor,
    Brightness
}

internal sealed class MonitorRowGlyph : Control
{
    private readonly MonitorRowGlyphKind _kind;
    private readonly DrawingResources _drawingResources = new();

    public MonitorRowGlyph(MonitorRowGlyphKind kind)
    {
        _kind = kind;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor |
            ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        TabStop = false;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = kind == MonitorRowGlyphKind.Monitor ? "Display" : "Brightness";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _drawingResources.EnsureDpi(DeviceDpi);
        float scale = DeviceDpi / 96f;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var pen = _drawingResources.Pen(ForeColor, scale);
        float x = Width / 2f;
        float y = Height / 2f;

        if (_kind == MonitorRowGlyphKind.Monitor)
        {
            g.DrawPath(pen, _drawingResources.RoundedRectangle(
                new RectangleF(x - 6 * scale, y - 5 * scale, 12 * scale, 8 * scale), scale));
            g.DrawLine(pen, x, y + 3 * scale, x, y + 6 * scale);
            g.DrawLine(pen, x - 3 * scale, y + 6 * scale, x + 3 * scale, y + 6 * scale);
        }
        else
        {
            g.DrawEllipse(pen, x - 2.5f * scale, y - 2.5f * scale, 5 * scale, 5 * scale);
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                float dx = (float)Math.Cos(angle) * scale;
                float dy = (float)Math.Sin(angle) * scale;
                g.DrawLine(pen, x + dx * 4.5f, y + dy * 4.5f, x + dx * 6.5f, y + dy * 6.5f);
            }
        }
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        _drawingResources.Clear();
        base.OnForeColorChanged(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _drawingResources.Dispose();
        base.Dispose(disposing);
    }
}
