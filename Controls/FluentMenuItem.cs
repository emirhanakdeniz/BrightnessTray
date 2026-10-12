namespace BrightnessTray.Controls;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BrightnessTray.UI;

public sealed class FluentMenuItem : ToolStripMenuItem
{
    private readonly DrawingResources _drawingResources = new();
    private readonly Font _bodyFont;
    private string? _glyph;
    private bool _isDarkMode = true;

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
        get => base.Checked;
        set
        {
            if (base.Checked != value)
            {
                base.Checked = value;
                Invalidate();
            }
        }
    }

    public bool IsDarkMode
    {
        get
        {
            if (Owner is FluentContextMenu menu) return menu.IsDarkMode;
            if (OwnerItem is FluentMenuItem parentItem) return parentItem.IsDarkMode;
            return _isDarkMode;
        }
        set
        {
            if (_isDarkMode != value)
            {
                _isDarkMode = value;
                _drawingResources.Clear();
                Invalidate();
                if (HasDropDownItems)
                {
                    for (int i = 0; i < DropDownItems.Count; i++)
                    {
                        if (DropDownItems[i] is FluentMenuItem sub)
                        {
                            sub.IsDarkMode = value;
                        }
                    }
                }
            }
        }
    }

    public FluentMenuItem(string text, string? glyph = null, EventHandler? onClick = null)
        : base(text, null, onClick)
    {
        _glyph = glyph;
        _bodyFont = FluentTypography.CreateBodyFont();
        Font = _bodyFont;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        float scale = ((Owner?.DeviceDpi ?? 0) > 0 ? Owner!.DeviceDpi : 96) / 96f;
        int minWidth = Theme.Scale(Theme.MenuMinWidth, scale);
        int height = Theme.Scale(Theme.MenuItemHeight, scale);

        int textWidth = TextRenderer.MeasureText(Text, Font).Width;
        int totalWidth = Math.Max(minWidth, textWidth + Theme.Scale(72, scale));

        return new Size(totalWidth, height);
    }

    protected override ToolStripDropDown CreateDefaultDropDown()
    {
        var dropDown = base.CreateDefaultDropDown();
        if (dropDown is ToolStripDropDownMenu dropDownMenu)
        {
            dropDownMenu.ShowImageMargin = false;
            dropDownMenu.ShowCheckMargin = false;
            dropDownMenu.DropShadowEnabled = false;

            float scale = ((Owner?.DeviceDpi ?? 0) > 0 ? Owner!.DeviceDpi : 96) / 96f;
            dropDownMenu.Padding = new Padding(Theme.Scale(Theme.MenuPadding, scale));

            dropDownMenu.Opening += (s, e) =>
            {
                if (Owner != null)
                {
                    dropDownMenu.Renderer = Owner.Renderer;
                }
            };

            dropDownMenu.HandleCreated += (s, e) =>
            {
                BrightnessTray.Native.DwmInterop.ApplyMenuWindowAttributes(dropDownMenu.Handle, IsDarkMode);
            };

            dropDownMenu.Opened += (s, e) =>
            {
                BrightnessTray.Native.DwmInterop.ApplyMenuWindowAttributes(dropDownMenu.Handle, IsDarkMode);
            };
        }
        return dropDown;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _drawingResources.EnsureDpi(Owner?.DeviceDpi ?? 96);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float scale = ((Owner?.DeviceDpi ?? 0) > 0 ? Owner!.DeviceDpi : 96) / 96f;
        int itemWidth = Width;
        int itemHeight = Height;

        bool isDark = IsDarkMode;

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

            Color hoverColor = Theme.MenuItemOverlay(isDark, Pressed);

            var path = _drawingResources.RoundedRectangle(hoverRect, radius);
            var brush = _drawingResources.Brush(hoverColor);
            g.FillPath(brush, path);
        }

        Color textColor = Enabled
            ? Theme.FlyoutText(isDark)
            : Theme.TextDisabled(isDark);

        // 1. Draw leading Fluent glyph if provided
        int textLeft = Theme.Space12(scale);
        if (!string.IsNullOrEmpty(_glyph))
        {
            int glyphSize = Theme.Scale(Theme.MenuIconSize, scale);
            var glyphRect = new Rectangle(textLeft, (itemHeight - glyphSize) / 2, glyphSize, glyphSize);

            var glyphFont = _drawingResources.GlyphFont(Theme.FontSizeGlyphSmall);
            Color iconColor = Enabled ? Theme.FlyoutIcon(isDark) : Theme.TextDisabled(isDark);
            _drawingResources.DrawText(g, _glyph, glyphFont, glyphRect, iconColor, ContentAlignment.MiddleCenter);

            textLeft += glyphSize + Theme.Space12(scale);
        }

        // 2. Draw right-side indicator: ChevronRight if HasDropDownItems, or CheckMark if Checked
        int textRightPadding = Theme.Space12(scale);
        if (HasDropDownItems)
        {
            int chevronSize = Theme.Scale(12, scale);
            int chevronX = itemWidth - Theme.Space12(scale) - chevronSize;
            var chevronRect = new Rectangle(chevronX, (itemHeight - chevronSize) / 2, chevronSize, chevronSize);
            var chevronFont = _drawingResources.GlyphFont(Theme.FontSizeGlyphSmall);
            Color chevronColor = Theme.FlyoutIcon(isDark);
            _drawingResources.DrawText(g, "\uE76C", chevronFont, chevronRect, chevronColor, ContentAlignment.MiddleCenter);
            textRightPadding = Theme.Space12(scale) + chevronSize + Theme.Space8(scale);
        }
        else if (Checked)
        {
            int checkSize = Theme.Scale(14, scale);
            int checkX = itemWidth - Theme.Space12(scale) - checkSize;
            var checkRect = new Rectangle(checkX, (itemHeight - checkSize) / 2, checkSize, checkSize);
            var checkFont = _drawingResources.GlyphFont(Theme.FontSizeGlyphSmall);
            Color checkColor = Theme.FlyoutSecondaryText(isDark);
            _drawingResources.DrawText(g, "\uE73E", checkFont, checkRect, checkColor, ContentAlignment.MiddleCenter);
            textRightPadding = Theme.Space12(scale) + checkSize + Theme.Space8(scale);
        }

        // 3. Draw item text
        var textRect = new Rectangle(
            textLeft,
            0,
            itemWidth - textLeft - textRightPadding,
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
