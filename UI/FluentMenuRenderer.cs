namespace BrightnessTray.UI;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BrightnessTray.Controls;

public sealed class FluentMenuRenderer : ToolStripRenderer, IDisposable
{
    private readonly DrawingResources _drawingResources = new();
    private bool _isDarkMode;

    public FluentMenuRenderer(bool isDarkMode)
    {
        _isDarkMode = isDarkMode;
    }

    public void UpdateTheme(bool isDarkMode)
    {
        _drawingResources.Clear();
        _isDarkMode = isDarkMode;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        _drawingResources.EnsureDpi(e.ToolStrip.DeviceDpi);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Windows 11 dark/light menu background surface
        Color bgColor = Theme.MenuBackground(_isDarkMode);

        var brush = _drawingResources.Brush(bgColor);
        g.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        _drawingResources.EnsureDpi(e.ToolStrip.DeviceDpi);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 1px subtle border matching Windows 11 menu flyouts
        Color borderColor = Theme.BorderSubtle(_isDarkMode);
        var pen = _drawingResources.Pen(borderColor, 1f);
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        g.DrawRectangle(pen, rect);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        _drawingResources.EnsureDpi(e.ToolStrip?.DeviceDpi ?? 96);
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float dpiScale = (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
        int insetH = Theme.Space4(dpiScale);
        int insetV = Math.Max(1, Theme.Scale(2, dpiScale));
        int radius = Theme.Radius4(dpiScale);

        int itemWidth = e.Item?.Width ?? 0;
        int itemHeight = e.Item?.Height ?? 0;

        var rect = new Rectangle(
            insetH,
            insetV,
            itemWidth - insetH * 2 - 1,
            itemHeight - insetV * 2 - 1);

        Color hoverColor = Theme.MenuItemOverlay(_isDarkMode, e.Item?.Pressed == true);

        var path = _drawingResources.RoundedRectangle(rect, radius);
        var brush = _drawingResources.Brush(hoverColor);
        g.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        _drawingResources.EnsureDpi(e.ToolStrip?.DeviceDpi ?? 96);
        var g = e.Graphics;
        float dpiScale = (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
        int marginH = Theme.Space12(dpiScale);
        int itemWidth = e.Item?.Width ?? 0;
        int itemHeight = e.Item?.Height ?? 0;
        int y = itemHeight / 2;

        Color sepColor = Theme.FlyoutDivider(_isDarkMode);
        var pen = _drawingResources.Pen(sepColor, 1f);
        g.DrawLine(pen, marginH, y, itemWidth - marginH, y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // FluentMenuItem and FluentToggleMenuItem handle their own text rendering
        if (e.Item is FluentMenuItem || e.Item is FluentToggleMenuItem)
        {
            return;
        }

        Color textColor = e.Item.Enabled
            ? Theme.FlyoutText(_isDarkMode)
            : Theme.TextDisabled(_isDarkMode);

        TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, textColor, flags);
    }

    public void Dispose() => _drawingResources.Dispose();
}
