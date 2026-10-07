namespace BrightnessTray.UI;

using System.Drawing;
using System.Windows.Forms;
using BrightnessTray.Controls;
using BrightnessTray.Native;

public sealed class FluentContextMenu : ContextMenuStrip
{
    private readonly FluentMenuRenderer _fluentRenderer;
    private bool _isDarkMode = true;
    private Color _accentColor = Color.FromArgb(0x00, 0x67, 0xC0);

    public bool IsDarkMode => _isDarkMode;
    public Color AccentColor => _accentColor;

    public FluentContextMenu()
        : this(!DwmInterop.IsAppsUseLightTheme(), DwmInterop.GetAccentColor())
    {
    }

    public FluentContextMenu(bool isDarkMode, Color? accentColor = null)
    {
        _isDarkMode = isDarkMode;
        _accentColor = accentColor ?? DwmInterop.GetAccentColor();

        _fluentRenderer = new FluentMenuRenderer(_isDarkMode, _accentColor);
        Renderer = _fluentRenderer;

        ShowImageMargin = false;
        ShowCheckMargin = false;
        AutoSize = true;
        DropShadowEnabled = false; // Disable legacy GDI shadow so DWM renders native blurred shadow

        float scale = (DeviceDpi > 0 ? DeviceDpi : 96) / 96f;
        Padding = new Padding(Theme.Space4(scale), Theme.Space4(scale), Theme.Space4(scale), Theme.Space4(scale));
        MinimumSize = new Size(Theme.Scale(240, scale), 0);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        float scale = (DeviceDpi > 0 ? DeviceDpi : 96) / 96f;
        int minWidth = Theme.Scale(240, scale);

        int maxItemWidth = minWidth;
        foreach (ToolStripItem item in Items)
        {
            var pref = item.GetPreferredSize(Size.Empty);
            if (pref.Width > maxItemWidth)
            {
                maxItemWidth = pref.Width;
            }
        }

        var baseSize = base.GetPreferredSize(proposedSize);
        return new Size(Math.Max(baseSize.Width, maxItemWidth), baseSize.Height);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int contentWidth = ClientRectangle.Width - Padding.Horizontal;
        foreach (ToolStripItem item in Items)
        {
            if (item is ToolStripMenuItem)
            {
                item.Width = contentWidth;
            }
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Remove CS_DROPSHADOW (0x00020000) to prevent square black shadow artifacts on rounded corners
            cp.ClassStyle &= ~0x00020000;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDwmAttributes();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyDwmAttributes();
    }

    private void ApplyDwmAttributes()
    {
        if (IsHandleCreated)
        {
            DwmInterop.ApplyMenuWindowAttributes(Handle, _isDarkMode);
        }
    }

    public void UpdateTheme(bool isDark, Color accentColor)
    {
        _isDarkMode = isDark;
        _accentColor = accentColor;

        _fluentRenderer.UpdateTheme(isDark, accentColor);

        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            if (item is FluentMenuItem fluentItem)
            {
                fluentItem.IsDarkMode = isDark;
            }
            else if (item is FluentToggleMenuItem toggleItem)
            {
                toggleItem.IsDarkMode = isDark;
                toggleItem.AccentColor = accentColor;
            }
        }

        if (IsHandleCreated)
        {
            ApplyDwmAttributes();
            Invalidate();
        }
    }
}
