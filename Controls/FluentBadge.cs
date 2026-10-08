namespace BrightnessTray.Controls;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BrightnessTray.UI;

public sealed class FluentBadge : Control
{
    private readonly DrawingResources _drawingResources = new();
    private readonly Font? _ownedFont;
    private bool _isDarkMode = true;
    private bool _isMicaActive;

    public FluentBadge(string text = "Software", Font? font = null)
    {
        Text = text;

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        _ownedFont = font == null ? FluentTypography.CreateBadgeFont() : null;
        Font = font ?? _ownedFont!;
        UpdateSize();
    }

    private int Scale(int pixels) => (int)Math.Round(pixels * (DeviceDpi / 96f));

    private void UpdateSize()
    {
        int padH = Scale(6);
        int padV = Scale(2);
        Size textSize = TextRenderer.MeasureText(Text, Font);
        Size = new Size(textSize.Width + padH * 2, textSize.Height + padV * 2);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateSize();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        UpdateSize();
    }

    public void UpdateTheme(bool isDark, bool isMica)
    {
        _drawingResources.Clear();
        _isDarkMode = isDark;
        _isMicaActive = isMica;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        base.OnPaintBackground(pevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _drawingResources.EnsureDpi(DeviceDpi);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int cornerRadius = Math.Max(2, Scale(3));
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        Color pillBg = _isDarkMode
            ? Color.FromArgb(35, 255, 255, 255)
            : Color.FromArgb(18, 0, 0, 0);

        Color textColor = _isDarkMode
            ? Color.FromArgb(200, 200, 200)
            : Color.FromArgb(85, 85, 85);

        var path = _drawingResources.RoundedRectangle(rect, cornerRadius);
        g.FillPath(_drawingResources.Brush(pillBg), path);

        _drawingResources.DrawText(
            g,
            Text,
            Font,
            rect,
            textColor,
            ContentAlignment.MiddleCenter);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        _drawingResources.Clear();
        UpdateSize();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _drawingResources.Dispose();
            _ownedFont?.Dispose();
        }
    }
}
