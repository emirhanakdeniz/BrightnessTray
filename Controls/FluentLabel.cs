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
    private bool _isDarkMode = true;

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

    public void UpdateTheme(bool isDark, bool isMica, Color foreColor)
    {
        _drawingResources.Clear();
        _isDarkMode = isDark;
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
        base.OnPaintBackground(pevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        _drawingResources.EnsureDpi(DeviceDpi);
        _drawingResources.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            TextAlign,
            AutoEllipsis);
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
