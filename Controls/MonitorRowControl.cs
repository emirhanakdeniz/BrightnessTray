namespace BrightnessTray.Controls;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BrightnessTray.Native;
using BrightnessTray.Services;

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
    private Color _accentColor = Color.FromArgb(0x00, 0x67, 0xC0);

    private SolidBrush? _cachedHoverBrush;

    public string MonitorId => _monitor.Id;
    public string DisplayName => _currentDisplayName;
    public bool IsEditing => _isEditing;

    private int Scale(int pixels) => (int)Math.Round(pixels * (DeviceDpi / 96f));

    public MonitorRowControl(
        MonitorInfo monitor,
        int displayIndex,
        BrightnessService brightnessService,
        SettingsService settingsService,
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
        Padding = new Padding(Scale(8), Scale(4), Scale(8), Scale(4));
        TabStop = false;

        // Top Row: [Monitor Icon U+E7F4] [Monitor Name (secondary)] [Pencil Glyph U+E70F] [Optional Software Badge]
        _topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = Scale(18),
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            TabStop = false
        };

        _monitorGlyph = new FluentLabel
        {
            Text = "\uE7F4", // Segoe Fluent Icons: TVMonitor / Screen
            Font = FluentTypography.CreateGlyphFont(9.5f),
            Dock = DockStyle.Left,
            Width = Scale(18),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            TabStop = false
        };

        if (_monitor.IsSoftware)
        {
            _softwareBadge = new FluentBadge("Software")
            {
                Dock = DockStyle.Right,
                Margin = Padding.Empty,
                TabStop = false
            };
            _topPanel.Controls.Add(_softwareBadge);
        }

        _pencilButton = new FluentLabel
        {
            Text = "\uE70F", // Segoe Fluent Icons: Edit / Pencil
            Font = FluentTypography.CreateGlyphFont(8.5f),
            Dock = DockStyle.Right,
            Width = Scale(18),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty,
            TabStop = false,
            Visible = false,
            Cursor = Cursors.Hand,
            AccessibleName = $"Rename {_currentDisplayName}"
        };
        _pencilButton.Click += (s, e) => BeginInlineEdit();
        _topPanel.Controls.Add(_pencilButton);

        _nameLabel = new FluentLabel
        {
            Text = _currentDisplayName,
            Font = FluentTypography.CreateSecondaryFont(),
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

        // Bottom Row: [Brightness Icon U+E706] [FluentSlider] [Percent Label]
        _bottomTable = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Scale(30),
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            TabStop = false
        };

        _bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(20)));
        _bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _bottomTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Scale(44)));

        _brightnessGlyph = new FluentLabel
        {
            Text = "\uE706", // Segoe Fluent Icons: Brightness
            Font = FluentTypography.CreateGlyphFont(9.5f),
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
            Height = Scale(30),
            Margin = Padding.Empty,
            TabStop = true,
            TabIndex = 0
        };

        _percentLabel = new FluentLabel
        {
            Text = $"{_monitor.CurrentPercent}%",
            Font = FluentTypography.CreateBodyFont(FontStyle.Bold),
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
            Invalidate();
        }
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

        Color fieldBg = _isDarkMode ? Color.FromArgb(45, 45, 48) : Color.FromArgb(240, 240, 240);
        Color textColor = _isDarkMode ? Color.White : Color.FromArgb(27, 27, 27);

        _inlineContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = fieldBg,
            Padding = new Padding(Scale(4), Scale(1), Scale(4), Scale(2)),
            Height = Scale(18),
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
        Padding = new Padding(Scale(8), Scale(4), Scale(8), Scale(4));
        _topPanel.Height = Scale(18);
        _monitorGlyph.Width = Scale(18);
        _pencilButton.Width = Scale(18);

        _bottomTable.Height = Scale(30);
        _bottomTable.ColumnStyles[0].Width = Scale(20);
        _bottomTable.ColumnStyles[2].Width = Scale(44);

        _slider.Height = Scale(30);
        Height = Scale(56);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Color bg = _isDarkMode ? Color.FromArgb(32, 32, 33) : Color.FromArgb(243, 243, 243);
        if (DwmInterop.IsHighContrast())
        {
            bg = SystemColors.Window;
        }
        e.Graphics.Clear(bg);

        if (_isRowHovered)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int radius = Math.Max(2, Scale(6));
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);

            using var path = CreateRoundedRectanglePath(rect, radius);
            if (_cachedHoverBrush != null)
            {
                g.FillPath(_cachedHoverBrush, path);
            }
        }
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

    public void UpdateTheme(bool isDark, bool isMica, Color accentColor)
    {
        _isDarkMode = isDark;
        _isMicaActive = isMica;
        _accentColor = accentColor;

        Color secondaryColor = FluentTypography.GetSecondaryTextColor(isDark);
        Color primaryColor = FluentTypography.GetPrimaryTextColor(isDark);

        _monitorGlyph.UpdateTheme(isDark, isMica, secondaryColor);
        _nameLabel.UpdateTheme(isDark, isMica, secondaryColor);
        _pencilButton.UpdateTheme(isDark, isMica, secondaryColor);
        _brightnessGlyph.UpdateTheme(isDark, isMica, secondaryColor);
        _percentLabel.UpdateTheme(isDark, isMica, primaryColor);

        _softwareBadge?.UpdateTheme(isDark, isMica);
        _slider.UpdateTheme(isDark, isMica, accentColor);

        Color highlightColor = isDark
            ? Color.FromArgb(12, 255, 255, 255)
            : Color.FromArgb(10, 0, 0, 0);

        _cachedHoverBrush?.Dispose();
        _cachedHoverBrush = new SolidBrush(highlightColor);

        Invalidate();
    }

    private void OnSliderValueChanged(object? sender, int value)
    {
        // Instant UI feedback with tabular number formatting
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
            _cachedHoverBrush?.Dispose();
            _cachedHoverBrush = null;

            _nameContextMenu.Dispose();
            CleanupInlineEditor();
        }

        base.Dispose(disposing);
    }
}
