namespace BrightnessTray.Controls;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

public sealed class FluentBadge : Control
{
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
        Font = font ?? FluentTypography.CreateBadgeFont();
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

        using (var path = CreateRoundedRectanglePath(rect, cornerRadius))
        {
            using var brush = new SolidBrush(pillBg);
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            rect,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        var arc = new Rectangle(rect.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
