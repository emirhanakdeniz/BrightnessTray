namespace BrightnessTray;

using System.Diagnostics;
using Microsoft.Win32;
using BrightnessTray.Controls;
using BrightnessTray.Native;
using BrightnessTray.Services;

public sealed class FlyoutForm : Form
{
    private const double OpenDurationMs = 167.0;
    private const double CloseDurationMs = 100.0;

    private readonly IBrightnessProvider _brightnessProvider;
    private readonly BrightnessService _brightnessService;
    private readonly SettingsService _settingsService;
    private readonly Panel _contentContainer;
    private NotifyIcon? _notifyIcon;

    private int _activeEditingCount;
    public bool IsInlineEditingActive => _activeEditingCount > 0;
    public event Action<string, string>? MonitorRenamed;

    private BackdropKind _currentBackdrop = BackdropKind.Mica;
    private bool _isMicaActive;
    private long _lastClosedTimestamp;
    private CancellationTokenSource? _displayChangeCts;
    private bool _isRefreshing;
    private bool _disposed;

    // Animation state - only created and running during active animation
    private System.Windows.Forms.Timer? _animTimer;
    private readonly Stopwatch _animStopwatch = new();
    private bool _isAnimating;
    private bool _isClosing;
    private Point _animStartLocation;
    private Point _animTargetLocation;
    private double _animStartOpacity;
    private double _animTargetOpacity;
    private double _animDurationMs;

    public float DpiScale => DeviceDpi / 96f;
    public int Scale(int pixels) => BrightnessTray.UI.Theme.Scale(pixels, DpiScale);

    public bool IsShowingOrVisible => Visible || _isAnimating;

    public BackdropKind CurrentBackdrop
    {
        get => _currentBackdrop;
        set
        {
            if (_currentBackdrop != value)
            {
                _currentBackdrop = value;
                if (IsHandleCreated)
                {
                    ApplyThemeAndBackdrop();
                }
            }
        }
    }

    public FlyoutForm(
        IBrightnessProvider brightnessProvider,
        BrightnessService brightnessService,
        SettingsService settingsService,
        NotifyIcon? notifyIcon = null)
    {
        _brightnessProvider = brightnessProvider;
        _brightnessService = brightnessService;
        _settingsService = settingsService;
        _notifyIcon = notifyIcon;

        // Window configuration
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = false; // Disabled on top-level form to avoid GDI double-buffer alpha conflicts with DWM

        _contentContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(BrightnessTray.UI.Theme.Space16(DpiScale)),
            TabStop = false
        };

        Controls.Add(_contentContainer);

        // Initial default bounds
        UpdateFormBounds(0);

        // Listen for system theme and display connect/disconnect events
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void SetNotifyIcon(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyThemeAndBackdrop();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _contentContainer.Padding = new Padding(BrightnessTray.UI.Theme.Space16(DpiScale));

        var table = _contentContainer.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
        if (table != null)
        {
            int count = table.RowCount;
            int rowHeight = Scale(60);
            int rowSpacing = BrightnessTray.UI.Theme.Space12(DpiScale);
            for (int i = 0; i < count; i++)
            {
                int currentSpacing = (i < count - 1) ? rowSpacing : 0;
                if (i < table.RowStyles.Count)
                {
                    table.RowStyles[i].Height = rowHeight + currentSpacing;
                }
            }
        }

        int monitorCount = table?.RowCount ?? 0;
        UpdateFormBounds(monitorCount);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (_isMicaActive)
        {
            // Clear with solid Color.Black so the DWM compositor renders the Mica backdrop through
            e.Graphics.Clear(Color.Black);
        }
        else
        {
            base.OnPaintBackground(e);
        }
    }

    public void ApplyThemeAndBackdrop()
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        bool isDark = !DwmInterop.IsAppsUseLightTheme();

        if (DwmInterop.IsMicaSupported() && CurrentBackdrop != BackdropKind.None)
        {
            bool applied = DwmInterop.ApplyBackdrop(Handle, CurrentBackdrop, isDark);
            if (applied)
            {
                _isMicaActive = true;
                BackColor = Color.Black;
            }
            else
            {
                ApplySolidFallback(isDark);
            }
        }
        else
        {
            ApplySolidFallback(isDark);
        }

        Color accentColor = DwmInterop.GetAccentColor();

        // Propagate theme to existing monitor rows
        foreach (var row in _contentContainer.Controls.OfType<TableLayoutPanel>()
            .SelectMany(t => t.Controls.OfType<MonitorRowControl>()))
        {
            row.UpdateTheme(isDark, _isMicaActive, accentColor);
        }

        Invalidate();
    }

    private void ApplySolidFallback(bool isDark)
    {
        _isMicaActive = false;
        if (DwmInterop.IsHighContrast())
        {
            BackColor = SystemColors.Window;
        }
        else
        {
            BackColor = BrightnessTray.UI.Theme.BackgroundBase(isDark);
        }
        Debug.WriteLine($"[FlyoutForm] Mica unavailable or disabled. Using solid theme-aware background fallback ({BackColor}).");
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(ApplyThemeAndBackdrop);
            }
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (IsInlineEditingActive)
        {
            return;
        }
        _lastClosedTimestamp = Stopwatch.GetTimestamp();
        CloseFlyout();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            CloseFlyout();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private const int WM_MOUSEWHEEL = 0x020A;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEWHEEL)
        {
            short delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            short screenX = (short)(m.LParam.ToInt64() & 0xFFFF);
            short screenY = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
            Point screenPt = new Point(screenX, screenY);

            var row = FindRowAtPoint(screenPt);
            if (row != null)
            {
                row.StepSlider(delta);
                m.Result = IntPtr.Zero;
                return;
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var row = FindRowAtPoint(Cursor.Position);
        row?.StepSlider(e.Delta);
    }

    private MonitorRowControl? FindRowAtPoint(Point screenPt)
    {
        var rows = _contentContainer.Controls.OfType<TableLayoutPanel>()
            .SelectMany(t => t.Controls.OfType<MonitorRowControl>())
            .ToList();

        if (rows.Count == 0)
        {
            return null;
        }

        foreach (var row in rows)
        {
            var rowScreenRect = row.RectangleToScreen(row.ClientRectangle);
            if (rowScreenRect.Contains(screenPt))
            {
                return row;
            }
        }

        if (rows.Count == 1)
        {
            return rows[0];
        }

        var formScreenRect = RectangleToScreen(ClientRectangle);
        if (formScreenRect.Contains(screenPt))
        {
            return rows.OrderBy(r =>
            {
                var rect = r.RectangleToScreen(r.ClientRectangle);
                if (screenPt.Y < rect.Top) return rect.Top - screenPt.Y;
                if (screenPt.Y > rect.Bottom) return screenPt.Y - rect.Bottom;
                return 0;
            }).FirstOrDefault();
        }

        return null;
    }

    public bool CanReopen()
    {
        if (_isClosing)
        {
            return false;
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - _lastClosedTimestamp;
        double elapsedSec = (double)elapsedTicks / Stopwatch.Frequency;
        return elapsedSec > 0.20; // 200ms guard window prevents click-away reopen loop
    }

    public void ShowFlyout(NotifyIcon? notifyIcon = null)
    {
        if (notifyIcon != null)
        {
            _notifyIcon = notifyIcon;
        }

        if (!CanReopen())
        {
            return;
        }

        PositionNearTaskbar();
        var targetLocation = Location;

        ApplyThemeAndBackdrop();

        if (!AnimationInterop.IsAnimationEnabled())
        {
            StopAndDisposeAnimTimer();
            _isAnimating = false;
            _isClosing = false;
            Opacity = 1.0;
            Location = targetLocation;
            Show();
            Activate();
            _ = RefreshMonitorsAsync();
            return;
        }

        Point startLocation = GetSlideOffsetLocation(targetLocation);

        if (!_isAnimating)
        {
            Opacity = 0.0;
            Location = startLocation;
            Show();
            Activate();
        }

        StartOpenAnimation(targetLocation, startLocation);
        _ = RefreshMonitorsAsync();
    }

    public void CloseFlyout()
    {
        if (!Visible || _isClosing)
        {
            return;
        }

        _lastClosedTimestamp = Stopwatch.GetTimestamp();

        if (!AnimationInterop.IsAnimationEnabled())
        {
            StopAndDisposeAnimTimer();
            _isAnimating = false;
            _isClosing = false;
            Hide();
            Opacity = 1.0;
            return;
        }

        Point closeOffset = GetSlideOffsetLocation(Location);
        StartCloseAnimation(closeOffset);
    }

    private void StartOpenAnimation(Point targetLocation, Point startLocation)
    {
        StopAndDisposeAnimTimer();
        _isClosing = false;
        _animStartLocation = _isAnimating ? Location : startLocation;
        _animTargetLocation = targetLocation;
        _animStartOpacity = _isAnimating ? Opacity : 0.0;
        _animTargetOpacity = 1.0;
        _animDurationMs = OpenDurationMs;

        _animStopwatch.Restart();
        _isAnimating = true;

        _animTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _animTimer.Tick += OnAnimTimerTick;
        _animTimer.Start();
    }

    private void StartCloseAnimation(Point targetLocation)
    {
        StopAndDisposeAnimTimer();
        _isClosing = true;
        _animStartLocation = Location;
        _animTargetLocation = targetLocation;
        _animStartOpacity = Opacity;
        _animTargetOpacity = 0.0;
        _animDurationMs = CloseDurationMs;

        _animStopwatch.Restart();
        _isAnimating = true;

        _animTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _animTimer.Tick += OnAnimTimerTick;
        _animTimer.Start();
    }

    private void StopAndDisposeAnimTimer()
    {
        if (_animTimer != null)
        {
            _animTimer.Stop();
            _animTimer.Tick -= OnAnimTimerTick;
            _animTimer.Dispose();
            _animTimer = null;
        }
    }

    private void OnAnimTimerTick(object? sender, EventArgs e)
    {
        double elapsed = _animStopwatch.Elapsed.TotalMilliseconds;
        double t = Math.Clamp(elapsed / _animDurationMs, 0.0, 1.0);

        // Ease curve: cubic ease-out for open, quadratic ease-in for close
        double ease = _isClosing
            ? Math.Pow(t, 2)
            : 1.0 - Math.Pow(1.0 - t, 3);

        Opacity = Math.Clamp(_animStartOpacity + (_animTargetOpacity - _animStartOpacity) * ease, 0.0, 1.0);

        int curX = (int)Math.Round(_animStartLocation.X + (_animTargetLocation.X - _animStartLocation.X) * ease);
        int curY = (int)Math.Round(_animStartLocation.Y + (_animTargetLocation.Y - _animStartLocation.Y) * ease);
        Location = new Point(curX, curY);

        if (t >= 1.0)
        {
            StopAndDisposeAnimTimer();
            _isAnimating = false;

            if (_isClosing)
            {
                Hide();
                _isClosing = false;
                Opacity = 1.0;
                _lastClosedTimestamp = Stopwatch.GetTimestamp();
            }
            else
            {
                Opacity = 1.0;
                Location = _animTargetLocation;
            }
        }
    }

    private Point GetSlideOffsetLocation(Point baseLocation)
    {
        int slide = Scale(12);
        var screen = Screen.FromPoint(baseLocation);
        var edge = DetectTaskbarEdge(screen.Bounds, screen.WorkingArea);

        return edge switch
        {
            TaskbarEdge.Top => new Point(baseLocation.X, baseLocation.Y - slide),
            TaskbarEdge.Left => new Point(baseLocation.X - slide, baseLocation.Y),
            TaskbarEdge.Right => new Point(baseLocation.X + slide, baseLocation.Y),
            _ => new Point(baseLocation.X, baseLocation.Y + slide) // Bottom or auto-hide: slide down
        };
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Cancel any pending debounce timer
        _displayChangeCts?.Cancel();
        _displayChangeCts?.Dispose();
        _displayChangeCts = new CancellationTokenSource();

        var token = _displayChangeCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait 1200ms to allow Windows display driver and DDC/CI / WMI bus to settle
                await Task.Delay(1200, token).ConfigureAwait(false);

                if (token.IsCancellationRequested || _disposed)
                {
                    return;
                }

                if (IsHandleCreated && !IsDisposed)
                {
                    BeginInvoke(async () =>
                    {
                        if (Visible)
                        {
                            await RefreshMonitorsAsync();
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by newer display settings event
            }
            catch
            {
                // Fail silently
            }
        });
    }

    public void SeedMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0 || _disposed || IsDisposed)
        {
            return;
        }

        var existingRows = _contentContainer.Controls.OfType<TableLayoutPanel>()
            .SelectMany(t => t.Controls.OfType<MonitorRowControl>())
            .ToList();

        bool matchesExisting = existingRows.Count == monitors.Count &&
            existingRows.Select(r => r.MonitorId).SequenceEqual(monitors.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

        if (matchesExisting)
        {
            for (int i = 0; i < monitors.Count; i++)
            {
                existingRows[i].UpdateBrightness(monitors[i].CurrentPercent);
            }
            return;
        }

        BuildMonitorRows(monitors);
    }

    private void BuildMonitorRows(IReadOnlyList<MonitorInfo> monitors)
    {
        _contentContainer.SuspendLayout();
        try
        {
            ClearContent();

            bool isDark = !DwmInterop.IsAppsUseLightTheme();
            Color accentColor = DwmInterop.GetAccentColor();

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = monitors.Count,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                TabStop = false
            };

            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int rowHeight = Scale(60);
            int rowSpacing = BrightnessTray.UI.Theme.Space12(DpiScale);

            for (int i = 0; i < monitors.Count; i++)
            {
                int currentSpacing = (i < monitors.Count - 1) ? rowSpacing : 0;
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight + currentSpacing));

                int displayIndex = i + 1;
                var row = new MonitorRowControl(
                    monitors[i],
                    displayIndex,
                    _brightnessService,
                    _settingsService,
                    onEditingStateChanged: isEditing =>
                    {
                        if (isEditing) _activeEditingCount++;
                        else _activeEditingCount = Math.Max(0, _activeEditingCount - 1);
                    },
                    onRenamed: (monitorId, newName) =>
                    {
                        MonitorRenamed?.Invoke(monitorId, newName);
                    })
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 0, 0, currentSpacing)
                };
                row.UpdateTheme(isDark, _isMicaActive, accentColor);
                table.Controls.Add(row, 0, i);
            }

            _contentContainer.Controls.Add(table);
        }
        finally
        {
            _contentContainer.ResumeLayout(true);
        }

        UpdateFormBounds(monitors.Count);
    }

    public async Task RefreshMonitorsAsync()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;

        try
        {
            // Query monitors off the UI thread via IBrightnessProvider
            var monitors = await Task.Run(() => _brightnessProvider.GetMonitors());

            if (_disposed || IsDisposed)
            {
                return;
            }

            var existingRows = _contentContainer.Controls.OfType<TableLayoutPanel>()
                .SelectMany(t => t.Controls.OfType<MonitorRowControl>())
                .ToList();

            if (monitors.Count == 0)
            {
                // Silently keep existing cached rows if available, never show empty state during a refresh
                if (existingRows.Count == 0)
                {
                    ShowEmptyState();
                }
                return;
            }

            // Check if existing rows already match current monitors
            bool matchesExisting = existingRows.Count == monitors.Count &&
                existingRows.Select(r => r.MonitorId).SequenceEqual(monitors.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

            if (matchesExisting)
            {
                // Update brightness values in existing controls silently without rebuilding
                for (int i = 0; i < monitors.Count; i++)
                {
                    existingRows[i].UpdateBrightness(monitors[i].CurrentPercent);
                }
                return;
            }

            // Rebuild rows dynamically
            BuildMonitorRows(monitors);
        }
        catch
        {
            // Fail silently, never crash
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ShowEmptyState()
    {
        _contentContainer.SuspendLayout();
        try
        {
            ClearContent();

            bool isDark = !DwmInterop.IsAppsUseLightTheme();

            var emptyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Padding = Padding.Empty,
                TabStop = false
            };

            var glyph = new FluentLabel
            {
                Text = "\uE7F4", // Monitor screen glyph
                Font = FluentTypography.CreateGlyphFont(BrightnessTray.UI.Theme.FontSizeGlyphLarge),
                Dock = DockStyle.Top,
                Height = Scale(36),
                TextAlign = ContentAlignment.MiddleCenter,
                TabStop = false
            };
            glyph.UpdateTheme(isDark, _isMicaActive, BrightnessTray.UI.Theme.TextSecondary(isDark));

            var title = new FluentLabel
            {
                Text = "No DDC/CI displays found",
                Font = FluentTypography.CreateBodyFont(FontStyle.Bold),
                Dock = DockStyle.Top,
                Height = Scale(22),
                TextAlign = ContentAlignment.MiddleCenter,
                TabStop = false
            };
            title.UpdateTheme(isDark, _isMicaActive, BrightnessTray.UI.Theme.TextPrimary(isDark));

            var message = new FluentLabel
            {
                Text = "Ensure DDC/CI is enabled in your monitor's OSD menu or connect a supported display.",
                Font = FluentTypography.CreateSecondaryFont(),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopCenter,
                TabStop = false
            };
            message.UpdateTheme(isDark, _isMicaActive, BrightnessTray.UI.Theme.TextSecondary(isDark));

            emptyPanel.Controls.Add(message);
            emptyPanel.Controls.Add(title);
            emptyPanel.Controls.Add(glyph);

            _contentContainer.Controls.Add(emptyPanel);
        }
        finally
        {
            _contentContainer.ResumeLayout(true);
        }

        UpdateFormBounds(0);
    }

    private void ClearContent()
    {
        foreach (Control control in _contentContainer.Controls)
        {
            control.Dispose();
        }
        _contentContainer.Controls.Clear();
    }

    private void UpdateFormBounds(int monitorCount)
    {
        int width = Scale(320);
        int contentHeight;

        if (monitorCount == 0)
        {
            contentHeight = BrightnessTray.UI.Theme.Space16(DpiScale) * 2 + Scale(100);
        }
        else
        {
            int paddingTotal = BrightnessTray.UI.Theme.Space16(DpiScale) * 2;
            int rowsTotal = monitorCount * Scale(60);
            int spacingTotal = Math.Max(0, monitorCount - 1) * BrightnessTray.UI.Theme.Space12(DpiScale);
            contentHeight = paddingTotal + rowsTotal + spacingTotal;
        }

        Size = new Size(width, contentHeight);
        PositionNearTaskbar();
    }

    public void PositionNearTaskbar()
    {
        int gap = BrightnessTray.UI.Theme.Space12(DpiScale);
        Rectangle iconRect = Rectangle.Empty;
        bool hasIconRect = _notifyIcon != null && ShellInterop.TryGetNotifyIconRect(_notifyIcon, out iconRect);

        Screen targetScreen;
        if (hasIconRect)
        {
            var iconCenter = new Point((iconRect.Left + iconRect.Right) / 2, (iconRect.Top + iconRect.Bottom) / 2);
            targetScreen = Screen.FromPoint(iconCenter);
        }
        else
        {
            targetScreen = Screen.FromPoint(Cursor.Position);
        }

        var bounds = targetScreen.Bounds;
        var workArea = targetScreen.WorkingArea;
        TaskbarEdge edge = DetectTaskbarEdge(bounds, workArea);

        int x;
        int y;

        if (hasIconRect)
        {
            int iconCenterX = (iconRect.Left + iconRect.Right) / 2;
            int iconCenterY = (iconRect.Top + iconRect.Bottom) / 2;

            // In auto-hide mode, workArea == bounds. Infer edge from iconRect proximity to screen edges
            if (edge == TaskbarEdge.None)
            {
                if (iconRect.Bottom >= bounds.Bottom - Scale(60)) edge = TaskbarEdge.Bottom;
                else if (iconRect.Top <= bounds.Top + Scale(60)) edge = TaskbarEdge.Top;
                else if (iconRect.Left <= bounds.Left + Scale(60)) edge = TaskbarEdge.Left;
                else if (iconRect.Right >= bounds.Right - Scale(60)) edge = TaskbarEdge.Right;
            }

            switch (edge)
            {
                case TaskbarEdge.Bottom:
                    y = Math.Min(workArea.Bottom, iconRect.Top) - Height;
                    x = iconCenterX - Width / 2;
                    x = Math.Clamp(x, bounds.Left + gap, bounds.Right - Width - gap);
                    break;

                case TaskbarEdge.Top:
                    y = Math.Max(workArea.Top, iconRect.Bottom);
                    x = iconCenterX - Width / 2;
                    x = Math.Clamp(x, bounds.Left + gap, bounds.Right - Width - gap);
                    break;

                case TaskbarEdge.Left:
                    x = Math.Max(workArea.Left, iconRect.Right);
                    y = iconCenterY - Height / 2;
                    y = Math.Clamp(y, bounds.Top + gap, bounds.Bottom - Height - gap);
                    break;

                case TaskbarEdge.Right:
                    x = Math.Min(workArea.Right, iconRect.Left) - Width;
                    y = iconCenterY - Height / 2;
                    y = Math.Clamp(y, bounds.Top + gap, bounds.Bottom - Height - gap);
                    break;

                default:
                    x = bounds.Right - Width - gap;
                    y = bounds.Bottom - Height - gap;
                    break;
            }
        }
        else
        {
            switch (edge)
            {
                case TaskbarEdge.Bottom:
                    x = workArea.Right - Width - gap;
                    y = workArea.Bottom - Height;
                    break;

                case TaskbarEdge.Top:
                    x = workArea.Right - Width - gap;
                    y = workArea.Top;
                    break;

                case TaskbarEdge.Left:
                    x = workArea.Left;
                    y = workArea.Bottom - Height - gap;
                    break;

                case TaskbarEdge.Right:
                    x = workArea.Right - Width;
                    y = workArea.Bottom - Height - gap;
                    break;

                default:
                    x = bounds.Right - Width - gap;
                    y = bounds.Bottom - Height - gap;
                    break;
            }
        }

        Location = new Point(x, y);
    }

    private static TaskbarEdge DetectTaskbarEdge(Rectangle bounds, Rectangle workArea)
    {
        if (workArea.Bottom < bounds.Bottom) return TaskbarEdge.Bottom;
        if (workArea.Top > bounds.Top) return TaskbarEdge.Top;
        if (workArea.Left > bounds.Left) return TaskbarEdge.Left;
        if (workArea.Right < bounds.Right) return TaskbarEdge.Right;
        return TaskbarEdge.None;
    }

    private enum TaskbarEdge
    {
        None,
        Bottom,
        Top,
        Left,
        Right
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

                _displayChangeCts?.Cancel();
                _displayChangeCts?.Dispose();
                _displayChangeCts = null;

                StopAndDisposeAnimTimer();

                ClearContent();
                _contentContainer.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
