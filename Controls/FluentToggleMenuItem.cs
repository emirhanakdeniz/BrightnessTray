namespace BrightnessTray.Controls;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BrightnessTray.UI;

public sealed class FluentToggleMenuItem : ToolStripMenuItem
{
    private readonly DrawingResources _drawingResources = new();
    private readonly Font _bodyFont;
    private string? _glyph;
    private bool _isDarkMode = true;
    private bool _isChecked;
    private Color _accentColor = Color.FromArgb(0x00, 0x67, 0xC0);

    public event EventHandler<bool>? ToggleChanged;

    public string? Glyph
    {
        get => _glyph;
        set
        {
            if (_glyph != value)
            {
                _glyph = value;
                Invalidate();
            }
        }
    }

    public new bool Checked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                Invalidate();
            }
        }
    }

    public bool IsDarkMode
    {
        get => Owner is FluentContextMenu menu ? menu.IsDarkMode : _isDarkMode;
        set
        {
            if (_isDarkMode != value)
            {
                _isDarkMode = value;
                _drawingResources.Clear();
                Invalidate();
            }
        }
    }

    public Color AccentColor
    {
        get => Owner is FluentContextMenu menu ? menu.AccentColor : _accentColor;
        set
        {
            if (_accentColor != value)
            {
                _accentColor = value;
                _drawingResources.Clear();
                Invalidate();
            }
        }
    }

    public FluentToggleMenuItem(string text, string? glyph = null, bool isChecked = false)
        : base(text)
    {
        _glyph = glyph;
        _isChecked = isChecked;
        _bodyFont = FluentTypography.CreateBodyFont();
        Font = _bodyFont;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        float scale = (Owner?.DeviceDpi ?? 96) / 96f;
        int minWidth = Theme.Scale(240, scale);
        int height = Theme.Scale(36, scale);

        int textWidth = TextRenderer.MeasureText(Text, Font).Width;
        int totalWidth = Math.Max(minWidth, textWidth + Theme.Scale(110, scale));

        return new Size(totalWidth, height);
    }

    protected override void OnClick(EventArgs e)
    {
        _isChecked = !_isChecked;
        Invalidate();
        ToggleChanged?.Invoke(this, _isChecked);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _drawingResources.EnsureDpi(Owner?.DeviceDpi ?? 96);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float scale = (Owner?.DeviceDpi ?? 96) / 96f;
        int itemWidth = Math.Max(Width, Owner?.ClientSize.Width ?? Width);
        int itemHeight = Math.Max(Height, Theme.Scale(36, scale));

        bool isDark = IsDarkMode;
        Color accent = AccentColor;

        // Draw hover pill if selected
        if (Selected && Enabled)
        {
            int insetH = Theme.Space4(scale);
            int insetV = Math.Max(1, Theme.Scale(2, scale));
            int radius = Theme.Radius4(scale);

            var hoverRect = new Rectangle(
                insetH,
                insetV,
                itemWidth - insetH * 2 - 1,
                itemHeight - insetV * 2 - 1);

            Color hoverColor = isDark
                ? Color.FromArgb(22, 255, 255, 255)
                : Color.FromArgb(14, 0, 0, 0);

            var path = _drawingResources.RoundedRectangle(hoverRect, radius);
            var brush = _drawingResources.Brush(hoverColor);
            g.FillPath(brush, path);
        }

        Color textColor = Enabled
            ? Theme.TextPrimary(isDark)
            : Theme.TextDisabled(isDark);

        // 1. Draw leading Fluent glyph
        int textLeft = Theme.Space12(scale);
        if (!string.IsNullOrEmpty(_glyph))
        {
            int glyphSize = Theme.Scale(16, scale);
            var glyphRect = new Rectangle(textLeft, (itemHeight - glyphSize) / 2, glyphSize, glyphSize);

            var glyphFont = _drawingResources.GlyphFont(Theme.FontSizeGlyphMedium);
            _drawingResources.DrawText(g, _glyph, glyphFont, glyphRect, textColor, ContentAlignment.MiddleCenter);

            textLeft += glyphSize + Theme.Space12(scale);
        }

        // 2. Draw toggle pill on right
        int toggleWidth = Theme.Scale(38, scale);
        int toggleHeight = Theme.Scale(20, scale);
        int toggleX = itemWidth - toggleWidth - Theme.Space12(scale);
        int toggleY = (itemHeight - toggleHeight) / 2;
        var toggleRect = new Rectangle(toggleX, toggleY, toggleWidth, toggleHeight);
        int toggleRadius = toggleHeight / 2;

        var pillPath = _drawingResources.RoundedRectangle(toggleRect, toggleRadius);
        if (_isChecked)
        {
            // Active state: Windows Accent Blue fill
            var fillBrush = _drawingResources.Brush(accent);
            g.FillPath(fillBrush, pillPath);

            // Circular white knob on the right
            int knobPad = Theme.Scale(3, scale);
            int knobSize = toggleHeight - knobPad * 2;
            int knobX = toggleX + toggleWidth - knobPad - knobSize;
            int knobY = toggleY + knobPad;

            var knobBrush = _drawingResources.Brush(Color.White);
            g.FillEllipse(knobBrush, knobX, knobY, knobSize, knobSize);
        }
        else
        {
            // Inactive state: subtle border and neutral dark/light background
            Color offTrackBg = isDark
                ? Color.FromArgb(32, 32, 32)
                : Color.FromArgb(235, 235, 235);
            Color offBorderColor = isDark
                ? Color.FromArgb(120, 120, 120)
                : Color.FromArgb(160, 160, 160);

            var trackBrush = _drawingResources.Brush(offTrackBg);
            g.FillPath(trackBrush, pillPath);

            var borderPen = _drawingResources.Pen(offBorderColor, 1.5f);
            g.DrawPath(borderPen, pillPath);

            // Circular muted knob on the left
            int knobPad = Theme.Scale(3, scale);
            int knobSize = toggleHeight - knobPad * 2;
            int knobX = toggleX + knobPad;
            int knobY = toggleY + knobPad;

            Color knobColor = isDark
                ? Color.FromArgb(200, 200, 200)
                : Color.FromArgb(90, 90, 90);

            var knobBrush = _drawingResources.Brush(knobColor);
            g.FillEllipse(knobBrush, knobX, knobY, knobSize, knobSize);
        }

        // 3. Draw item text between glyph and toggle
        int availableTextWidth = toggleX - textLeft - Theme.Space8(scale);
        var textRect = new Rectangle(
            textLeft,
            0,
            availableTextWidth,
            itemHeight);

        _drawingResources.DrawText(g, Text ?? string.Empty, Font, textRect, textColor, ContentAlignment.MiddleLeft);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _drawingResources.Dispose();
            _bodyFont.Dispose();
        }
    }
}
