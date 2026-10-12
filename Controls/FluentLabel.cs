namespace BrightnessTray.Controls;

using System.Drawing;
using System.Windows.Forms;
using BrightnessTray.UI;

public sealed class FluentLabel : Control
{
    private readonly DrawingResources _drawingResources = new();
    private ContentAlignment _textAlign = ContentAlignment.MiddleLeft;
    private bool _autoEllipsis;
    private bool _isMicaActive;

    public ContentAlignment TextAlign
    {
        get => _textAlign;
        set
        {
            if (_textAlign != value)
            {
                _textAlign = value;
                Invalidate();
            }
        }
    }

    public bool AutoEllipsis
    {
        get => _autoEllipsis;
        set
        {
            if (_autoEllipsis != value)
            {
                _autoEllipsis = value;
                Invalidate();
            }
        }
    }

    public FluentLabel(Font? font = null)
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.StandardClick |
            ControlStyles.StandardDoubleClick, true);

        BackColor = Color.Transparent;
        if (font != null)
        {
            Font = font;
        }
    }

    public void UpdateTheme(bool isMica, Color foreColor)
    {
        _drawingResources.Clear();
        _isMicaActive = isMica;
        ForeColor = foreColor;
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // Parent painting can turn DWM's black glass surface into an opaque buffered strip.
        if (_isMicaActive)
            pevent.Graphics.Clear(Color.FromArgb(0, 0, 0, 0));
        else
            base.OnPaintBackground(pevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        _drawingResources.EnsureDpi(DeviceDpi);
        // ClearType assumes an opaque background; Mica needs grayscale coverage in the alpha channel.
        var renderingHint = _isMicaActive
            ? System.Drawing.Text.TextRenderingHint.AntiAliasGridFit
            : System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _drawingResources.DrawText(
            e.Graphics,
            Text,
            Font,
            new Rectangle(Padding.Left, Padding.Top,
                Math.Max(0, ClientSize.Width - Padding.Horizontal),
                Math.Max(0, ClientSize.Height - Padding.Vertical)),
            ForeColor,
            TextAlign,
            AutoEllipsis,
            renderingHint);
    }

    protected override void OnForeColorChanged(EventArgs e)
    {
        _drawingResources.Clear();
        base.OnForeColorChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _drawingResources.Dispose();
        base.Dispose(disposing);
    }
}
