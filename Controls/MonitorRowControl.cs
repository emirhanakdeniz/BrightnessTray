namespace BrightnessTray.Controls;

using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using BrightnessTray.Native;
using BrightnessTray.Services;
using BrightnessTray.UI;

public sealed class MonitorRowControl : Panel, IMessageFilter
{
    private readonly MonitorInfo _monitor;
    private readonly int _displayIndex;
    private readonly BrightnessService _brightnessService;
    private readonly SettingsService _settingsService;
    private readonly Action<bool>? _onEditingStateChanged;
    private readonly Action<string, string>? _onRenamed;

    private readonly FluentLabel _nameLabel;
    private readonly FluentSlider _slider;
    private readonly FluentLabel _percentLabel;
    private readonly MonitorRowGlyph _monitorGlyph;
    private readonly MonitorRowGlyph _brightnessGlyph;
    private readonly MonitorExpandButton _expandButton;
    private readonly ToolTip _nameToolTip = new();

    private readonly Panel _topPanel;
    private readonly Panel _sliderPanel;
    private readonly FluentContextMenu _nameContextMenu;
    private readonly ToolStripMenuItem _renameMenuItem;
    private readonly ToolStripMenuItem _resetMenuItem;
    private readonly ToolStripMenuItem _expandMenuItem;

    private Panel? _inlineContainer;
    private TextBox? _inlineTextBox;
    private Form? _editingForm;

    private string _currentDisplayName;
    private bool _isUpdatingUI;
    private bool _isDarkMode = true;
    private bool _isMicaActive;
    private bool _isEditing;
    private bool _isExpanded = true;
    private bool _showBottomDivider;
    private Color _accentColor = Color.FromArgb(0x00, 0x67, 0xC0);

    private readonly DrawingResources _drawingResources = new();
    private readonly Font _editorFont;

    public string MonitorId => _monitor.Id;
    public string DisplayName => _currentDisplayName;
    public bool IsEditing => _isEditing;
    public bool IsExpanded => _isExpanded;
    internal int PreferredRowHeight => Scale(_isExpanded ? Theme.MonitorRowHeight : Theme.CollapsedMonitorRowHeight);
    [DefaultValue(false)]
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
        _editorFont = gdiCache.BodyFont;
        _onEditingStateChanged = onEditingStateChanged;
        _onRenamed = onRenamed;

        _currentDisplayName = MonitorNameResolver.Resolve(_monitor.Id, _monitor.Name, _displayIndex, _settingsService);

        DoubleBuffered = true;
        BackColor = Color.Transparent;
        Margin = Padding.Empty;
        Padding = new Padding(Scale(Theme.MonitorRowHorizontalPadding), Scale(8),
            Scale(Theme.MonitorRowHorizontalPadding), Scale(8));
        TabStop = false;

        // Keep the clickable monitor name visible when collapsed.
        _topPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = Scale(26),
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            TabStop = false
        };

        _nameLabel = new FluentLabel(gdiCache.MonitorNameFont)
        {
            Text = _currentDisplayName,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty,
            Padding = new Padding(Scale(4), 0, 0, 0),
            TabStop = false,
            Cursor = Cursors.Hand,
            AccessibleName = _currentDisplayName,
            AccessibleDescription = $"{_currentDisplayName}. Click to rename, or press F2."
        };

        _nameLabel.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                BeginInlineEdit();
            }
        };

        // Keep reset and collapse actions available from the name’s context menu.
        _nameContextMenu = new FluentContextMenu();
        _renameMenuItem = new FluentMenuItem("Rename", "\uE70F", (s, e) => BeginInlineEdit());
        _resetMenuItem = new FluentMenuItem("Reset name", "\uE777", (s, e) => ResetAlias());
        _expandMenuItem = new FluentMenuItem("Collapse", "\uE70E", (s, e) => ToggleExpanded());
        _nameContextMenu.Items.Add(_renameMenuItem);
        _nameContextMenu.Items.Add(_resetMenuItem);
        _nameContextMenu.Items.Add(new ToolStripSeparator());
        _nameContextMenu.Items.Add(_expandMenuItem);

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

        _monitorGlyph = new MonitorRowGlyph(MonitorRowGlyphKind.Monitor)
        {
            Dock = DockStyle.Left,
            Width = Scale(24)
        };
        _expandButton = new MonitorExpandButton
        {
            Dock = DockStyle.Right,
            Width = Scale(28),
            TabIndex = 0
        };
        _expandButton.Click += (s, e) => ToggleExpanded();
        _topPanel.Controls.Add(_nameLabel);
        _topPanel.Controls.Add(_monitorGlyph);
        _topPanel.Controls.Add(_expandButton);
        _nameToolTip.SetToolTip(_nameLabel, _currentDisplayName);

        // Keep the glyph, slider and percentage aligned on a separate compact line.
        _sliderPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Scale(26),
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            TabStop = false
        };

        _slider = new FluentSlider
        {
            Minimum = 0,
            Maximum = 100,
            Dock = DockStyle.Fill,
            Height = Scale(26),
            Margin = Padding.Empty,
            TabStop = true,
            TabIndex = 1,
            AccessibleName = $"Brightness for {_currentDisplayName}"
        };

        _percentLabel = new FluentLabel(gdiCache.SecondaryFont)
        {
            Text = $"{_monitor.CurrentPercent}%",
            Dock = DockStyle.Right,
            Width = Scale(40),
            Padding = Padding.Empty,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = Padding.Empty,
            TabStop = false
        };

        _brightnessGlyph = new MonitorRowGlyph(MonitorRowGlyphKind.Brightness)
        {
            Dock = DockStyle.Left,
            Width = Scale(24)
        };
        _sliderPanel.Controls.Add(_slider);
        _sliderPanel.Controls.Add(_brightnessGlyph);
        _sliderPanel.Controls.Add(_percentLabel);

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

        Controls.Add(_sliderPanel);
        Controls.Add(_topPanel);
        _topPanel.TabIndex = 0;
        _sliderPanel.TabIndex = 1;
        UpdateAccessibleNames();

        Height = PreferredRowHeight;

        // Keep wheel adjustment available across the entire row.
        HookEvents(this);
    }

    public void StepSlider(int delta)
    {
        _slider.StepValue(delta);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs h)
        {
            h.Handled = true;
        }
        base.OnMouseWheel(e);
        StepSlider(e.Delta);
    }

    private void HookEvents(Control control)
    {
        if (control != this && control != _slider)
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
        if (control != this && control != _slider)
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
        if (e is HandledMouseEventArgs h)
        {
            h.Handled = true;
        }
        StepSlider(e.Delta);
    }

    public void ToggleExpanded()
    {
        if (_isEditing)
        {
            return;
        }

        _isExpanded = !_isExpanded;
        _sliderPanel.Visible = _isExpanded;
        _expandButton.IsExpanded = _isExpanded;
        _expandMenuItem.Text = _isExpanded ? "Collapse" : "Expand";
        ((FluentMenuItem)_expandMenuItem).Glyph = _isExpanded ? "\uE70E" : "\uE70D";
        UpdateAccessibleNames();
        Height = PreferredRowHeight;
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

        Color fieldBg = Theme.FieldBackground(_isDarkMode);
        Color textColor = Theme.TextPrimary(_isDarkMode);

        _inlineContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = fieldBg,
            Padding = new Padding(Theme.Space4(DpiScale), Theme.Space4(DpiScale), Theme.Space4(DpiScale), Theme.Space4(DpiScale)),
            Height = Scale(32),
            TabStop = false
        };

        _inlineTextBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = _editorFont,
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
            int lineY = _inlineContainer.Height - Scale(1);
            var accentPen = _drawingResources.Pen(_accentColor, Scale(2));
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

        // Non-focusable labels and empty space do not trigger TextBox.LostFocus.
        // Watch mouse presses only while the editor exists, without consuming the click.
        Application.AddMessageFilter(this);
        _editingForm = FindForm();
        if (_editingForm != null)
            _editingForm.Deactivate += OnEditingFormDeactivate;

        _inlineTextBox.Focus();
        _inlineTextBox.SelectAll();
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (!_isEditing || _inlineContainer == null) return false;

        // Client and non-client left, right, middle, and extra-button presses.
        if (m.Msg is 0x0201 or 0x0204 or 0x0207 or 0x020B
            or 0x00A1 or 0x00A4 or 0x00A7 or 0x00AB)
        {
            Control? target = Control.FromChildHandle(m.HWnd);
            if (target != _inlineContainer &&
                (target == null || !_inlineContainer.Contains(target)))
            {
                CommitInlineEdit(_inlineTextBox?.Text ?? _currentDisplayName);
            }
        }

        return false;
    }

    private void OnEditingFormDeactivate(object? sender, EventArgs e)
    {
        CommitInlineEdit(_inlineTextBox?.Text ?? _currentDisplayName);
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
        UpdateAccessibleNames();

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
        Application.RemoveMessageFilter(this);
        if (_editingForm != null)
        {
            _editingForm.Deactivate -= OnEditingFormDeactivate;
            _editingForm = null;
        }

        if (_inlineContainer != null)
        {
            _topPanel.Controls.Remove(_inlineContainer);
            _inlineContainer.Dispose();
            _inlineContainer = null;
            _inlineTextBox = null;
        }

        _nameLabel.Visible = true;
    }

    public void ResetAlias()
    {
        _settingsService.SetAlias(_monitor.Id, null);
        _currentDisplayName = MonitorNameResolver.Resolve(_monitor.Id, _monitor.Name, _displayIndex, _settingsService);
        _nameLabel.Text = _currentDisplayName;
        _nameLabel.AccessibleName = _currentDisplayName;
        UpdateAccessibleNames();
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

    private void UpdateAccessibleNames()
    {
        _nameLabel.AccessibleDescription = $"{_currentDisplayName}. Click to rename, or press F2.";
        _slider.AccessibleName = $"Brightness for {_currentDisplayName}";
        _expandButton.AccessibleName = $"{(_isExpanded ? "Collapse" : "Expand")} {_currentDisplayName}";
        _expandButton.AccessibleDescription = _isExpanded ? "Hide brightness slider" : "Show brightness slider";
        _nameToolTip.SetToolTip(_nameLabel, _currentDisplayName);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpiMetrics();
    }

    private void ApplyDpiMetrics()
    {
        _drawingResources.Clear();
        Padding = new Padding(Scale(Theme.MonitorRowHorizontalPadding), Scale(8),
            Scale(Theme.MonitorRowHorizontalPadding), Scale(8));
        _topPanel.Height = Scale(26);
        _nameLabel.Padding = new Padding(Scale(4), 0, 0, 0);
        _monitorGlyph.Width = Scale(24);
        _brightnessGlyph.Width = Scale(24);
        _expandButton.Width = Scale(28);
        _percentLabel.Width = Scale(40);
        _sliderPanel.Height = Scale(26);
        _slider.Height = Scale(26);
        Height = PreferredRowHeight;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        _drawingResources.EnsureDpi(DeviceDpi);
        if (!_isMicaActive)
        {
            Color bg = DwmInterop.IsHighContrast() ? SystemColors.Window : Theme.BackgroundBase(_isDarkMode);
            e.Graphics.Clear(bg);
        }
        else
        {
            e.Graphics.Clear(Color.FromArgb(0, 0, 0, 0));
        }

        if (_showBottomDivider)
        {
            var divPen = _drawingResources.Pen(Theme.FlyoutDivider(_isDarkMode), Math.Max(1, Scale(1)));
            int inset = Scale(Theme.MonitorRowHorizontalPadding);
            e.Graphics.DrawLine(divPen, inset, Height - 1, Width - inset, Height - 1);
        }
    }

    public void UpdateTheme(bool isDark, bool isMica, Color accentColor)
    {
        _drawingResources.Clear();
        _isDarkMode = isDark;
        _isMicaActive = isMica;
        _accentColor = accentColor;

        Color primaryColor = Theme.FlyoutText(isDark);
        Color iconColor = Theme.FlyoutIcon(isDark);

        _nameLabel.UpdateTheme(isMica, primaryColor);
        _percentLabel.UpdateTheme(isMica, Theme.FlyoutSecondaryText(isDark));
        _monitorGlyph.ForeColor = iconColor;
        _brightnessGlyph.ForeColor = Theme.FlyoutBrightnessIcon(isDark);
        _expandButton.BackColor = DwmInterop.IsHighContrast() ? SystemColors.Window : Theme.BackgroundBase(isDark);
        _expandButton.ForeColor = iconColor;

        _slider.UpdateTheme(isDark, accentColor);
        _nameContextMenu.UpdateTheme(isDark, accentColor);
        if (_inlineContainer != null && _inlineTextBox != null)
        {
            Color fieldBackground = Theme.FieldBackground(isDark);
            _inlineContainer.BackColor = fieldBackground;
            _inlineTextBox.BackColor = fieldBackground;
            _inlineTextBox.ForeColor = primaryColor;
            _inlineContainer.Invalidate();
        }

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
            _drawingResources.Dispose();

            _nameContextMenu.Dispose();
            _nameToolTip.Dispose();
            CleanupInlineEditor();
        }

        base.Dispose(disposing);
    }
}
