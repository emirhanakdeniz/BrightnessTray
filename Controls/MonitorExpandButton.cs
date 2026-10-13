namespace BrightnessTray.Controls;

using System.ComponentModel;
using System.Drawing.Drawing2D;
using BrightnessTray.UI;

internal sealed class MonitorExpandButton : Button
{
    private readonly DrawingResources _drawingResources = new();
    private bool _isExpanded = true;
    private bool _isHovered;

    [DefaultValue(true)]
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Invalidate();
        }
    }

    public MonitorExpandButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw, true);
        BackColor = Theme.BackgroundBase(true);
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _drawingResources.EnsureDpi(DeviceDpi);
        var g = e.Graphics;
        // ButtonBase is Opaque: OnPaint owns the background as well as the glyph.
        // Clear recycled buffer pixels before drawing the chevron and state overlays.
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f;
        float x = Width / 2f;
        float y = Height / 2f;
        Color glyphColor = _isHovered ? Theme.FlyoutText(BackColor.GetBrightness() < 0.5f) : ForeColor;
        var pen = _drawingResources.Pen(glyphColor, scale);
        if (_isHovered)
        {
            g.FillPath(_drawingResources.Brush(Color.FromArgb(18, ForeColor)),
                _drawingResources.RoundedRectangle(new RectangleF(1, 1, Width - 2, Height - 2), 4 * scale));
        }
        float direction = _isExpanded ? 1 : -1;
        g.DrawLine(pen, x - 3 * scale, y - direction * 1.5f * scale, x, y + direction * 1.5f * scale);
        g.DrawLine(pen, x, y + direction * 1.5f * scale, x + 3 * scale, y - direction * 1.5f * scale);
        if (Focused && ShowFocusCues)
        {
            g.DrawPath(pen, _drawingResources.RoundedRectangle(
                new RectangleF(scale, scale, Width - 2 * scale, Height - 2 * scale), 4 * scale));
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = false;
        Invalidate();
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
