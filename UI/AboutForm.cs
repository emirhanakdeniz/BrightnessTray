namespace BrightnessTray.UI;

using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using BrightnessTray.Controls;
using BrightnessTray.Native;

internal sealed class AboutForm : Form
{
    private const string RepositoryUrl = "https://github.com/emirhanakdeniz/BrightnessTray";

    private readonly Font _bodyFont = FluentTypography.CreateFont(12);
    private readonly Font _headingFont = FluentTypography.CreateFont(18, FontStyle.Bold);
    private readonly Font _closeGlyphFont = FluentTypography.CreateGlyphFont(12);
    private readonly Image _appImage;
    private readonly List<Label> _secondaryLabels = [];
    private readonly List<Panel> _dividers = [];
    private readonly List<AboutButton> _buttons = [];
    private Point? _dragOrigin;

    public AboutForm()
    {
        Text = "About BrightnessTray";
        AccessibleName = Text;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(580, 0);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = _bodyFont;
        var assembly = typeof(AboutForm).Assembly;
        using (var stream = assembly.GetManifestResourceStream("BrightnessTray.AppIcon")!)
        using (var source = Image.FromStream(stream))
            _appImage = new Bitmap(source);

        var root = CreateTable();
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var header = CreateTable(2);
        header.Padding = new Padding(24, 12, 16, 12);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = CreateLabel(Text, _bodyFont);
        title.Anchor = AnchorStyles.Left;
        foreach (Control surface in new Control[] { header, title })
        {
            surface.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) _dragOrigin = Cursor.Position - (Size)Location; };
            surface.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left && _dragOrigin is Point origin) Location = Cursor.Position - (Size)origin; };
            surface.MouseUp += (_, _) => _dragOrigin = null;
        }
        var dismiss = CreateButton("\uE711", "Close About BrightnessTray", Close);
        dismiss.Font = _closeGlyphFont;
        dismiss.MinimumSize = new Size(32, 32);
        dismiss.Size = new Size(32, 32);
        dismiss.Borderless = true;
        header.Controls.Add(title);
        header.Controls.Add(dismiss);
        root.Controls.Add(header);
        root.Controls.Add(CreateDivider());

        var body = CreateTable(2);
        body.Padding = new Padding(24, 28, 24, 28);
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.Controls.Add(new PictureBox
        {
            Image = _appImage, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(120, 120),
            Margin = Padding.Empty, AccessibleName = "BrightnessTray app icon", TabStop = false
        });
        var details = CreateTable();
        details.Controls.Add(CreateLabel("BrightnessTray", _headingFont));
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "Unknown";
        var versionLabel = CreateLabel($"Version {version}", _bodyFont, secondary: true);
        versionLabel.Margin = new Padding(0, 8, 0, 20);
        details.Controls.Add(versionLabel);
        details.Controls.Add(CreateLabel("Monitor brightness control for Windows 11.", _bodyFont, secondary: true));
        details.Controls.Add(CreateLabel("External displays via DDC/CI; built-in panels via WMI.", _bodyFont, secondary: true));
        body.Controls.Add(details);
        root.Controls.Add(body);

        var footer = CreateTable(2);
        footer.Padding = new Padding(24, 0, 24, 24);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var divider = CreateDivider();
        footer.Controls.Add(divider);
        footer.SetColumnSpan(divider, 2);
        var maker = CreateLabel("by ea.", _bodyFont, secondary: true);
        maker.Margin = new Padding(0, 20, 0, 24);
        footer.Controls.Add(maker, 0, 1);
        footer.SetColumnSpan(maker, 2);
        var github = CreateButton("GitHub  ↗", "Open BrightnessTray on GitHub", OpenRepository);
        github.MinimumSize = new Size(132, 44);
        github.Anchor = AnchorStyles.Left;
        footer.Controls.Add(github, 0, 2);
        var close = CreateButton("Close", "Close About BrightnessTray", Close);
        close.MinimumSize = new Size(120, 44);
        close.IsPrimary = true;
        footer.Controls.Add(close, 1, 2);
        AcceptButton = close;
        CancelButton = close;
        root.Controls.Add(footer);
        Controls.Add(root);
        UpdateTheme();
        Shown += (_, _) => close.Focus();
    }

    private static TableLayoutPanel CreateTable(int columns = 1) => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = columns, Dock = DockStyle.Fill, Margin = Padding.Empty
    };

    private Label CreateLabel(string text, Font font, bool secondary = false)
    {
        var label = new Label { Text = text, Font = font, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 4) };
        if (secondary) _secondaryLabels.Add(label);
        return label;
    }

    private Panel CreateDivider()
    {
        var divider = new Panel { Height = 1, Dock = DockStyle.Top, Margin = Padding.Empty };
        _dividers.Add(divider);
        return divider;
    }

    private AboutButton CreateButton(string text, string accessibleName, Action action)
    {
        var button = new AboutButton { Text = text, AccessibleName = accessibleName, Font = _bodyFont, Margin = Padding.Empty };
        button.Click += (_, _) => action();
        _buttons.Add(button);
        return button;
    }

    private void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"Unable to open the browser.\n{RepositoryUrl}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    internal void UpdateTheme()
    {
        ApplyTheme(!DwmInterop.IsAppsUseLightTheme());
    }

    private void ApplyTheme(bool isDark)
    {
        BackColor = Theme.MenuBackground(isDark);
        ForeColor = Theme.FlyoutText(isDark);
        foreach (var label in _secondaryLabels) label.ForeColor = Theme.FlyoutSecondaryText(isDark);
        foreach (var divider in _dividers) divider.BackColor = Theme.FlyoutDivider(isDark);
        foreach (var button in _buttons) button.UpdateTheme(isDark);
        if (IsHandleCreated) DwmInterop.ApplyMenuWindowAttributes(Handle, isDark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateTheme();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _appImage.Dispose();
            _bodyFont.Dispose();
            _headingFont.Dispose();
            _closeGlyphFont.Dispose();
        }
    }

    private sealed class AboutButton : Button
    {
        private readonly DrawingResources _drawingResources = new();
        internal bool IsPrimary;
        internal bool Borderless;
        private bool _isDark;
        private bool _hovered;
        private Color _focusColor;

        internal AboutButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
        }

        internal void UpdateTheme(bool isDark)
        {
            _drawingResources.Clear();
            _isDark = isDark;
            _focusColor = SystemInformation.HighContrast ? SystemColors.Highlight : DwmInterop.GetAccentColor();
            ForeColor = Theme.FlyoutText(isDark);
            BackColor = Theme.MenuBackground(isDark);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            _drawingResources.EnsureDpi(DeviceDpi);
            float scale = DeviceDpi / 96f;
            float inset = Math.Max(1, scale);
            var bounds = new RectangleF(inset, inset, Width - 2 * inset - 1, Height - 2 * inset - 1);
            var path = _drawingResources.RoundedRectangle(bounds, Theme.Scale(4, scale));
            bool highContrast = SystemInformation.HighContrast;
            Color fill = IsPrimary && !highContrast
                ? (_isDark ? Color.FromArgb(90, 68, 52) : Color.FromArgb(238, 220, 203)) : BackColor;
            e.Graphics.FillPath(_drawingResources.Brush(fill), path);
            if (_hovered)
            {
                e.Graphics.FillPath(_drawingResources.Brush(Theme.MenuItemOverlay(_isDark, pressed: false)), path);
            }
            bool showFocus = Focused && ShowFocusCues;
            if (!Borderless || showFocus)
            {
                Color border = showFocus ? _focusColor : highContrast ? SystemColors.WindowText
                    : IsPrimary ? Color.FromArgb(185, 132, 94) : Theme.BorderSubtle(_isDark);
                e.Graphics.DrawPath(_drawingResources.Pen(border, showFocus ? 2 * scale : scale), path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _drawingResources.Dispose();
        }
    }
}
