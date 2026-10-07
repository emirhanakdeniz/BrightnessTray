namespace BrightnessTray.Controls;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BrightnessTray.Native;
using BrightnessTray.Services;
using BrightnessTray.UI;

public sealed class MonitorRowControl : Panel
{
    private readonly MonitorInfo _monitor;
    private readonly int _displayIndex;
    private readonly BrightnessService _brightnessService;
    private readonly SettingsService _settingsService;
    private readonly Action<bool>? _onEditingStateChanged;
    private readonly Action<string, string>? _onRenamed;

    private readonly FluentLabel _monitorGlyph;
    private readonly FluentLabel _nameLabel;
    private readonly FluentLabel _pencilButton;
    private readonly FluentLabel _chevronButton;
    private readonly FluentBadge? _softwareBadge;
    private readonly FluentLabel _brightnessGlyph;
    private readonly FluentSlider _slider;
    private readonly FluentLabel _percentLabel;

    private readonly Panel _topPanel;
    private readonly TableLayoutPanel _bottomTable;
    private readonly ContextMenuStrip _nameContextMenu;
    private readonly ToolStripMenuItem _renameMenuItem;
    private readonly ToolStripMenuItem _resetMenuItem;

    private Panel? _inlineContainer;
    private TextBox? _inlineTextBox;

    private string _currentDisplayName;
    private bool _isUpdatingUI;
    private bool _isDarkMode = true;
    private bool _isMicaActive;
    private bool _isRowHovered;
    private bool _isEditing;
    private bool _isExpanded = true;
    private bool _showBottomDivider;
    private Color _accentColor = Color.FromArgb(0x00, 0x67, 0xC0);

    private SolidBrush? _cachedHoverBrush;

    public string MonitorId => _monitor.Id;
    public string DisplayName => _currentDisplayName;
    public bool IsEditing => _isEditing;
    public bool IsExpanded => _isExpanded;
    public bool ShowBottomDivider
    {
        get => _showBottomDivider;
        set
        {
            if (_showBottomDivider != value)
            {
                _showBottomDivider = value;
                Invalidate();
            }
        }
    }

    public event Action? ExpandedChanged;

    private float DpiScale => DeviceDpi / 96f;
    private int Scale(int pixels) => Theme.Scale(pixels, DpiScale);

    public MonitorRowControl(
        MonitorInfo monitor,
        int displayIndex,
        BrightnessService brightnessService,
        SettingsService settingsService,
        FlyoutGdiCache gdiCache,
        Action<bool>? onEditingStateChanged = null,
        Action<string, string>? onRenamed = null)
    {
        _monitor = monitor;
        _displayIndex = displayIndex;
        _brightnessService = brightnessService;
        _settingsService = settingsService;
        _onEditingStateChanged = onEditingStateChanged;
        _onRenamed = onRenamed;

        _currentDisplayName = MonitorNameResolver.Resolve(_monitor.Id, _monitor.Name, _displayIndex, _settingsService);

        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Margin = Padding.Empty;
        Padding = new Padding(Theme.Space8(DpiScale), Theme.Space4(DpiScale), Theme.Space8(DpiScale), Theme.Space4(DpiScale));
        TabStop = false;

        // Top Row: [Monitor Icon U+E7F4] [Monitor Name (12px secondary)] [Pencil Glyph U+E70F] [Optional Software Badge]
        _topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = Scale(20),
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            TabStop = false
        };

        _monitorGlyph = new FluentLabel(gdiCache.GlyphMediumFont)
        {
            Text = "\uE7F4", // Segoe Fluent Icons: TVMonitor / Screen
            Dock = DockStyle.Left,
            Width = Scale(20),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            TabStop = false
        };

        _chevronButton = new FluentLabel(gdiCache.GlyphSmallFont)
        {
            Text = "\uE70D", // Segoe Fluent Icons: ChevronDown
            Dock = DockStyle.Right,
            Width = Scale(20),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty,
            TabStop = false,
            Cursor = Cursors.Hand,
            AccessibleName = $"Collapse {_currentDisplayName} section"
        };
        _chevronButton.Click += (s, e) => ToggleExpanded();
        _topPanel.Controls.Add(_chevronButton);

        if (_monitor.IsSoftware)
        {
            _softwareBadge = new FluentBadge("Software", gdiCache.BadgeFont)
            {
                Dock = DockStyle.Right,
                Margin = Padding.Empty,
                TabStop = false
            };
            _topPanel.Controls.Add(_softwareBadge);
        }

        _pencilButton = new FluentLabel(gdiCache.GlyphSmallFont)
        {
            Text = "\uE70F", // Segoe Fluent Icons: Edit / Pencil
            Dock = DockStyle.Right,
            Width = Scale(20),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty,
            TabStop = false,
            Visible = false,
            Cursor = Cursors.Hand,
            AccessibleName = $"Rename {_currentDisplayName}"
        };
        _pencilButton.Click += (s, e) => BeginInlineEdit();
        _topPanel.Controls.Add(_pencilButton);

        _topPanel.Click += (s, e) =>
        {
            if (!_isEditing)
            {
                ToggleExpanded();
            }
        };

        _nameLabel = new FluentLabel(gdiCache.SecondaryFont)
        {
            Text = _currentDisplayName,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            TabStop = false,
            AccessibleName = _currentDisplayName
        };

        _nameLabel.MouseDoubleClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                BeginInlineEdit();
            }
        };

        // Context Menu on name and top panel: "Rename" and "Reset name"
        _nameContextMenu = new ContextMenuStrip();
        _renameMenuItem = new ToolStripMenuItem("Rename", null, (s, e) => BeginInlineEdit());
        _resetMenuItem = new ToolStripMenuItem("Reset name", null, (s, e) => ResetAlias());
        _nameContextMenu.Items.Add(_renameMenuItem);
        _nameContextMenu.Items.Add(_resetMenuItem);

        _nameContextMenu.Opening += (s, e) =>
        {
            _resetMenuItem.Visible = MonitorNameResolver.HasCustomAlias(_monitor.Id, _settingsService);
            _onEditingStateChanged?.Invoke(true);
        };
        _nameContextMenu.Closed += (s, e) =>
        {
            _onEditingStateChanged?.Invoke(false);
        };

        _nameLabel.ContextMenuStrip = _nameContextMenu;
        _topPanel.ContextMenuStrip = _nameContextMenu;

        _topPanel.Controls.Add(_nameLabel);
        _topPanel.Controls.Add(_monitorGlyph);

        // Bottom Row: [Brightness Icon U+E706] [FluentSlider] [Percent Label (14px primary tabular)]
        _bottomTable = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Scale(32),
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            TabStop = false
        };

        _bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(20)));
        _bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(48)));

        _brightnessGlyph = new FluentLabel(gdiCache.GlyphMediumFont)
        {
            Text = "\uE706", // Segoe Fluent Icons: Brightness
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty,
            TabStop = false
        };

        _slider = new FluentSlider
        {
            Minimum = 0,
            Maximum = 100,
            Dock = DockStyle.Fill,
            Height = Scale(32),
            Margin = Padding.Empty,
            TabStop = true,
            TabIndex = 0
        };

        _percentLabel = new FluentLabel(gdiCache.BodyBoldFont)
        {
            Text = $"{_monitor.CurrentPercent}%",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = Padding.Empty,
            TabStop = false
        };

        _bottomTable.Controls.Add(_brightnessGlyph, 0, 0);
        _bottomTable.Controls.Add(_slider, 1, 0);
        _bottomTable.Controls.Add(_percentLabel, 2, 0);

        _isUpdatingUI = true;
        try
        {
            _slider.Value = Math.Clamp(_monitor.CurrentPercent, 0, 100);
        }
        finally
        {
            _isUpdatingUI = false;
        }

        _slider.ValueChanged += OnSliderValueChanged;

        Controls.Add(_bottomTable);
        Controls.Add(_topPanel);

        Height = Scale(56);

        // Hook recursive mouse enter/leave and mouse wheel
        HookEvents(this);
    }

    public void StepSlider(int delta)
    {
        _slider.StepValue(delta);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        StepSlider(e.Delta);
    }

    private void HookEvents(Control control)
    {
        control.MouseEnter += OnChildMouseEnter;
        control.MouseLeave += OnChildMouseLeave;
        if (control != _slider && control != _pencilButton)
        {
            control.MouseWheel += OnChildMouseWheel;
        }
        foreach (Control child in control.Controls)
        {
            HookEvents(child);
        }
    }

    private void UnhookEvents(Control control)
    {
        control.MouseEnter -= OnChildMouseEnter;
        control.MouseLeave -= OnChildMouseLeave;
        if (control != _slider && control != _pencilButton)
        {
            control.MouseWheel -= OnChildMouseWheel;
        }
        foreach (Control child in control.Controls)
        {
            UnhookEvents(child);
        }
    }

    private void OnChildMouseWheel(object? sender, MouseEventArgs e)
    {
        StepSlider(e.Delta);
    }

    private void OnChildMouseEnter(object? sender, EventArgs e)
    {
        SetHovered(true);
    }

    private void OnChildMouseLeave(object? sender, EventArgs e)
    {
        Point clientPos = PointToClient(Cursor.Position);
        if (!ClientRectangle.Contains(clientPos))
        {
            SetHovered(false);
        }
    }

    private void SetHovered(bool hovered)
    {
        if (_isRowHovered != hovered)
        {
            _isRowHovered = hovered;
            if (!_isEditing)
            {
                _pencilButton.Visible = hovered;
            }

            Color iconColor = hovered ? Theme.TextPrimary(_isDarkMode) : Theme.TextSecondary(_isDarkMode);
            _monitorGlyph.ForeColor = iconColor;
            _pencilButton.ForeColor = iconColor;
            _chevronButton.ForeColor = iconColor;

            Invalidate();
        }
    }

    public void ToggleExpanded()
    {
        if (_isEditing)
        {
            return;
        }

        _isExpanded = !_isExpanded;
        _bottomTable.Visible = _isExpanded;
        _chevronButton.Text = _isExpanded ? "\uE70D" : "\uE76C";
        _chevronButton.AccessibleName = _isExpanded
            ? $"Collapse {_currentDisplayName} section"
            : $"Expand {_currentDisplayName} section";

        Height = _isExpanded ? Scale(58) : Scale(26);
        ExpandedChanged?.Invoke();
        Invalidate();
    }

    public void BeginInlineEdit()
    {
        if (_isEditing)
        {
            return;
        }

        _isEditing = true;
        _onEditingStateChanged?.Invoke(true);

        _nameLabel.Visible = false;
        _pencilButton.Visible = false;

        Color fieldBg = Theme.FieldBackground(_isDarkMode);
        Color textColor = Theme.TextPrimary(_isDarkMode);

        _inlineContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = fieldBg,
            Padding = new Padding(Theme.Space4(DpiScale), 1, Theme.Space4(DpiScale), Theme.Space4(DpiScale)),
            Height = Scale(20),
            TabStop = false
        };

        _inlineTextBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = FluentTypography.CreateSecondaryFont(),
            BackColor = fieldBg,
            ForeColor = textColor,
            Text = _currentDisplayName,
            MaxLength = 32,
            Dock = DockStyle.Fill,
            AccessibleName = $"Edit name for {_currentDisplayName}"
        };

        _inlineContainer.Paint += (s, pe) =>
        {
            var g = pe.Graphics;
            int lineY = _inlineContainer.Height - 1;
            using var accentPen = new Pen(_accentColor, 2f);
            g.DrawLine(accentPen, 0, lineY, _inlineContainer.Width, lineY);
        };

        _inlineTextBox.KeyDown += (s, ke) =>
        {
            if (ke.KeyCode == Keys.Enter)
            {
                ke.Handled = true;
                ke.SuppressKeyPress = true;
                CommitInlineEdit(_inlineTextBox.Text);
            }
            else if (ke.KeyCode == Keys.Escape)
            {
                ke.Handled = true;
                ke.SuppressKeyPress = true;
                CancelInlineEdit();
            }
        };

        _inlineTextBox.LostFocus += (s, fe) =>
        {
            if (_isEditing)
            {
                CommitInlineEdit(_inlineTextBox?.Text ?? _currentDisplayName);
            }
        };

        _inlineContainer.Controls.Add(_inlineTextBox);
        _topPanel.Controls.Add(_inlineContainer);
        _inlineContainer.BringToFront();

        _inlineTextBox.Focus();
        _inlineTextBox.SelectAll();
    }

    public void CommitInlineEdit(string newText)
    {
        if (!_isEditing)
        {
            return;
        }

        _isEditing = false;

        // Persist alias atomically through SettingsService
        _settingsService.SetAlias(_monitor.Id, newText);

        _currentDisplayName = MonitorNameResolver.Resolve(_monitor.Id, _monitor.Name, _displayIndex, _settingsService);
        _nameLabel.Text = _currentDisplayName;
        _nameLabel.AccessibleName = _currentDisplayName;
        _pencilButton.AccessibleName = $"Rename {_currentDisplayName}";

        CleanupInlineEditor();
        _onRenamed?.Invoke(_monitor.Id, _currentDisplayName);
        _onEditingStateChanged?.Invoke(false);
    }

    public void CancelInlineEdit()
    {
        if (!_isEditing)
        {
            return;
        }

        _isEditing = false;
        CleanupInlineEditor();
        _onEditingStateChanged?.Invoke(false);
    }

    private void CleanupInlineEditor()
    {
        if (_inlineContainer != null)
        {
            _topPanel.Controls.Remove(_inlineContainer);
            _inlineContainer.Dispose();
            _inlineContainer = null;
            _inlineTextBox = null;
        }

        _nameLabel.Visible = true;
        _pencilButton.Visible = _isRowHovered;
    }

    public void ResetAlias()
    {
        _settingsService.SetAlias(_monitor.Id, null);
        _currentDisplayName = MonitorNameResolver.Resolve(_monitor.Id, _monitor.Name, _displayIndex, _settingsService);
        _nameLabel.Text = _currentDisplayName;
        _nameLabel.AccessibleName = _currentDisplayName;
        _pencilButton.AccessibleName = $"Rename {_currentDisplayName}";
        _onRenamed?.Invoke(_monitor.Id, _currentDisplayName);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F2 && !_isEditing)
        {
            BeginInlineEdit();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpiMetrics();
    }

    private void ApplyDpiMetrics()
    {
        float scale = DpiScale;
        Padding = new Padding(Theme.Space8(scale), Theme.Space4(scale), Theme.Space8(scale), Theme.Space4(scale));
        _topPanel.Height = Scale(20);
        _monitorGlyph.Width = Scale(20);
        _chevronButton.Width = Scale(20);
        _pencilButton.Width = Scale(20);

        _bottomTable.Height = Scale(32);
        _bottomTable.ColumnStyles[0].Width = Scale(20);
        _bottomTable.ColumnStyles[2].Width = Scale(48);

        _slider.Height = Scale(32);
        Height = _isExpanded ? Scale(58) : Scale(26);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!_isMicaActive)
        {
            Color bg = DwmInterop.IsHighContrast() ? SystemColors.Window : Theme.BackgroundBase(_isDarkMode);
            e.Graphics.Clear(bg);
        }
        else
        {
            e.Graphics.Clear(Color.FromArgb(0, 0, 0, 0));
        }

        if (_isRowHovered)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int radius = Theme.Radius8(DpiScale);
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            using var path = Theme.CreateRoundedRectanglePath(rect, radius);
            if (_cachedHoverBrush != null)
            {
                g.FillPath(_cachedHoverBrush, path);
            }
        }

        if (_showBottomDivider)
        {
            using var divPen = new Pen(Theme.BorderSubtle(_isDarkMode), 1f);
            e.Graphics.DrawLine(divPen, Theme.Space8(DpiScale), Height - 1, Width - Theme.Space8(DpiScale), Height - 1);
        }
    }

    public void UpdateTheme(bool isDark, bool isMica, Color accentColor)
    {
        _isDarkMode = isDark;
        _isMicaActive = isMica;
        _accentColor = accentColor;

        Color primaryColor = Theme.TextPrimary(isDark);
        Color secondaryColor = Theme.TextSecondary(isDark);

        Color iconColor = _isRowHovered ? primaryColor : secondaryColor;
        _monitorGlyph.UpdateTheme(isDark, isMica, iconColor);
        _nameLabel.UpdateTheme(isDark, isMica, primaryColor);
        _pencilButton.UpdateTheme(isDark, isMica, iconColor);
        _chevronButton.UpdateTheme(isDark, isMica, iconColor);
        _brightnessGlyph.UpdateTheme(isDark, isMica, primaryColor);
        _percentLabel.UpdateTheme(isDark, isMica, primaryColor);

        _softwareBadge?.UpdateTheme(isDark, isMica);
        _slider.UpdateTheme(isDark, isMica, accentColor);

        Color highlightColor = Theme.HoverOverlay(isDark);
        _cachedHoverBrush?.Dispose();
        _cachedHoverBrush = new SolidBrush(highlightColor);

        Invalidate();
    }

    private void OnSliderValueChanged(object? sender, int value)
    {
        // Instant optimistic feedback with tabular number formatting
        _percentLabel.Text = $"{value}%";

        if (_isUpdatingUI)
        {
            return;
        }

        // Debounced independent hardware request
        _brightnessService.RequestBrightness(_monitor.Id, value);
    }

    public void UpdateBrightness(int percent)
    {
        _isUpdatingUI = true;
        try
        {
            _slider.Value = Math.Clamp(percent, 0, 100);
            _percentLabel.Text = $"{_slider.Value}%";
        }
        finally
        {
            _isUpdatingUI = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UnhookEvents(this);
            _slider.ValueChanged -= OnSliderValueChanged;
            _cachedHoverBrush?.Dispose();
            _cachedHoverBrush = null;

            _nameContextMenu.Dispose();
            CleanupInlineEditor();
        }

        base.Dispose(disposing);
    }
}
