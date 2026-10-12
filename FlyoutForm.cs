namespace BrightnessTray;

using System.Diagnostics;
using BrightnessTray.Controls;
using BrightnessTray.Native;
using BrightnessTray.Services;
using BrightnessTray.UI;

public sealed class FlyoutForm : Form
{
    private const double OpenDurationMs = 167.0;
    private const double CloseDurationMs = 100.0;

    private readonly IBrightnessProvider _brightnessProvider;
    private readonly BrightnessService _brightnessService;
    private readonly SettingsService _settingsService;
    private readonly Panel _contentContainer;
    private readonly FlyoutGdiCache _gdiCache;
    private readonly SynchronizationContext _uiSyncContext;
    private NotifyIcon? _notifyIcon;
    private FluentLabel? _emptyStateGlyph;
    private FluentLabel? _emptyStateTitle;
    private FluentLabel? _emptyStateMessage;

    private int _activeEditingCount;
    public bool IsInlineEditingActive => _activeEditingCount > 0;
    public event Action<string, string>? MonitorRenamed;
    public event Action? FlyoutClosed;

    private BackdropKind _currentBackdrop = BackdropKind.None;
    private bool _isMicaActive;
    private bool _isDarkMode;
    private long _lastClosedTimestamp;
    private bool _isRefreshing;
    private CancellationTokenSource? _refreshCancellation;
    private bool _disposed;
    private TaskbarEdge _currentTaskbarEdge = TaskbarEdge.Bottom;
    private int _desiredHeight;

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
    public bool IsDarkMode => _isDarkMode;

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
        _uiSyncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settingsService = settingsService;
        _isDarkMode = !DwmInterop.IsAppsUseLightTheme();
        _notifyIcon = notifyIcon;
        _gdiCache = new FlyoutGdiCache();

        // Window configuration
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = false; // Disabled on top-level form to avoid GDI double-buffer alpha conflicts with DWM
        Padding = new Padding(0, Scale(Theme.FlyoutVerticalPadding), 0, Scale(Theme.FlyoutVerticalPadding));
        AccessibleName = "Display brightness";
        _contentContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AutoScroll = true,
            TabIndex = 1,
            TabStop = false
        };

        Controls.Add(_contentContainer);

        // A static loading state avoids showing a false empty state during discovery.
        ShowStatusState(loading: true);
        _brightnessService.BrightnessChanged += OnBrightnessRequested;
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
        Padding = new Padding(0, Scale(Theme.FlyoutVerticalPadding), 0, Scale(Theme.FlyoutVerticalPadding));
        if (_emptyStateGlyph != null && _emptyStateTitle != null)
        {
            _emptyStateGlyph.Height = Scale(36);
            _emptyStateTitle.Height = Scale(28);
            _contentContainer.Controls[0].Height = Scale(112);
            _contentContainer.Controls[0].Padding = new Padding(Scale(16), Scale(8), Scale(16), Scale(8));
        }
        RecalculateLayout();
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

        _isDarkMode = !DwmInterop.IsAppsUseLightTheme();
        bool isDark = IsDarkMode;

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

        // Propagate theme to existing monitor rows without LINQ allocations
        if (_contentContainer.Controls.Count > 0 && _contentContainer.Controls[0] is TableLayoutPanel table)
        {
            for (int i = 0; i < table.Controls.Count; i++)
            {
                if (table.Controls[i] is MonitorRowControl row)
                {
                    row.UpdateTheme(isDark, _isMicaActive, accentColor);
                }
            }
        }

        _emptyStateGlyph?.UpdateTheme(_isMicaActive, BrightnessTray.UI.Theme.TextSecondary(isDark));
        _emptyStateTitle?.UpdateTheme(_isMicaActive, BrightnessTray.UI.Theme.TextPrimary(isDark));
        _emptyStateMessage?.UpdateTheme(_isMicaActive, BrightnessTray.UI.Theme.TextSecondary(isDark));
        Invalidate();
    }

    private void ApplySolidFallback(bool isDark)
    {
        DwmInterop.ClearBackdrop(Handle);
        DwmInterop.ApplyMenuWindowAttributes(Handle, isDark);
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

    public void OnThemeOrColorChanged()
    {
        if (IsHandleCreated && !IsDisposed)
        {
            ApplyThemeAndBackdrop();
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

    private MonitorRowControl? FindRowAtPoint(Point screenPt)
    {
        // Header/scrollbar wheel input must not adjust an unrelated monitor.
        if (!_contentContainer.RectangleToScreen(_contentContainer.ClientRectangle).Contains(screenPt))
            return null;
        if (_contentContainer.Controls.Count == 0 || _contentContainer.Controls[0] is not TableLayoutPanel table)
        {
            return null;
        }

        for (int i = 0; i < table.Controls.Count; i++)
        {
            if (table.Controls[i] is MonitorRowControl row)
            {
                var rowScreenRect = row.RectangleToScreen(row.ClientRectangle);
                if (rowScreenRect.Contains(screenPt))
                {
                    return row;
                }
            }
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

        if (!IsHandleCreated)
        {
            CreateHandle();
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
        _refreshCancellation?.Cancel();

        if (!AnimationInterop.IsAnimationEnabled())
        {
            StopAndDisposeAnimTimer();
            _isAnimating = false;
            _isClosing = false;
            Hide();
            Opacity = 1.0;
            FlyoutClosed?.Invoke();
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
                FlyoutClosed?.Invoke();
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
        int slide = BrightnessTray.UI.Theme.FlyoutGap(DpiScale);

        return _currentTaskbarEdge switch
        {
            TaskbarEdge.Top => new Point(baseLocation.X, baseLocation.Y - slide),
            TaskbarEdge.Left => new Point(baseLocation.X - slide, baseLocation.Y),
            TaskbarEdge.Right => new Point(baseLocation.X + slide, baseLocation.Y),
            _ => new Point(baseLocation.X, baseLocation.Y + slide) // Bottom or auto-hide: slide down toward taskbar
        };
    }

    public void OnDisplayConfigurationChanged()
    {
        if (IsHandleCreated && !IsDisposed && Visible)
        {
            _ = RefreshMonitorsAsync();
        }
    }

    private static bool AreMonitorsMatching(List<MonitorRowControl> existingRows, IReadOnlyList<MonitorInfo> monitors)
    {
        if (existingRows.Count != monitors.Count) return false;
        for (int i = 0; i < existingRows.Count; i++)
        {
            if (!string.Equals(existingRows[i].MonitorId, monitors[i].Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private List<MonitorRowControl> GetExistingRows()
    {
        var list = new List<MonitorRowControl>();
        if (_contentContainer.Controls.Count > 0 && _contentContainer.Controls[0] is TableLayoutPanel table)
        {
            for (int i = 0; i < table.Controls.Count; i++)
            {
                if (table.Controls[i] is MonitorRowControl row)
                {
                    list.Add(row);
                }
            }
        }
        return list;
    }

    public void SeedMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0 || _disposed || IsDisposed)
        {
            return;
        }

        var existingRows = GetExistingRows();

        if (AreMonitorsMatching(existingRows, monitors))
        {
            for (int i = 0; i < monitors.Count; i++)
            {
                existingRows[i].UpdateBrightness(_brightnessService.ResolveReadBrightness(
                    monitors[i].Id, monitors[i].CurrentPercent, 0));
            }
            return;
        }

        BuildMonitorRows(monitors);
    }

    private void BuildMonitorRows(IReadOnlyList<MonitorInfo> monitors, long readVersion = 0)
    {
        _contentContainer.SuspendLayout();
        try
        {
            ClearContent();

            bool isDark = IsDarkMode;
            Color accentColor = DwmInterop.GetAccentColor();

            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                RowCount = monitors.Count,
                BackColor = Color.Transparent,
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                TabStop = false
            };

            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int rowHeight = Scale(Theme.MonitorRowHeight);

            for (int i = 0; i < monitors.Count; i++)
            {
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));

                int displayIndex = i + 1;
                var row = new MonitorRowControl(
                    monitors[i],
                    displayIndex,
                    _brightnessService,
                    _settingsService,
                    _gdiCache,
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
                    Margin = Padding.Empty,
                    ShowBottomDivider = i < monitors.Count - 1
                };
                row.ExpandedChanged += RecalculateLayout;
                row.UpdateTheme(isDark, _isMicaActive, accentColor);
                row.UpdateBrightness(_brightnessService.ResolveReadBrightness(
                    monitors[i].Id, monitors[i].CurrentPercent, readVersion));
                table.Controls.Add(row, 0, i);
            }

            _contentContainer.Controls.Add(table);
        }
        finally
        {
            _contentContainer.ResumeLayout(true);
        }

        RecalculateLayout();
    }

    private void RecalculateLayout()
    {
        if (_contentContainer.Controls.Count > 0 && _contentContainer.Controls[0] is TableLayoutPanel table)
        {
            int totalRowsHeight = 0;

            for (int i = 0; i < table.Controls.Count; i++)
            {
                if (table.Controls[i] is MonitorRowControl row)
                {
                    int h = row.PreferredRowHeight;
                    row.Margin = Padding.Empty;
                    if (i < table.RowStyles.Count)
                    {
                        table.RowStyles[i].Height = h;
                    }
                    totalRowsHeight += h;
                }
            }

            table.Height = totalRowsHeight;
            _contentContainer.AutoScrollMinSize = new Size(0, totalRowsHeight);
            UpdateFormBounds(table.RowCount);
        }
        else UpdateFormBounds(0);
    }

    public async Task RefreshMonitorsAsync()
    {
        if (_isRefreshing || _disposed || IsDisposed)
        {
            return;
        }

        _isRefreshing = true;
        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;

        try
        {
            // Query monitors off the UI thread via IBrightnessProvider
            long readVersion = _brightnessService.CaptureReadVersion();
            var monitors = await Task.Run(() => _brightnessProvider.GetMonitors(), cancellation.Token);

            if (_disposed || IsDisposed || cancellation.IsCancellationRequested)
            {
                return;
            }

            var existingRows = GetExistingRows();

            if (monitors.Count == 0)
            {
                // Silently keep existing cached rows if available, never show empty state during a refresh
                if (existingRows.Count == 0)
                {
                    ShowStatusState(loading: false);
                }
                return;
            }

            // Check if existing rows already match current monitors
            if (AreMonitorsMatching(existingRows, monitors))
            {
                // Update brightness values in existing controls silently without rebuilding
                for (int i = 0; i < monitors.Count; i++)
                {
                    existingRows[i].UpdateBrightness(_brightnessService.ResolveReadBrightness(
                        monitors[i].Id, monitors[i].CurrentPercent, readVersion));
                }
                return;
            }

            // Rebuild rows dynamically
            BuildMonitorRows(monitors, readVersion);
        }
        catch (OperationCanceledException)
        {
            // A closed flyout has no need for a queued hardware read.
        }
        catch
        {
            // Fail silently, never crash
            if (!_disposed && !IsDisposed && !cancellation.IsCancellationRequested && GetExistingRows().Count == 0)
                ShowStatusState(loading: false, failed: true);
        }
        finally
        {
            _refreshCancellation = null;
            _isRefreshing = false;
        }
    }

    private void OnBrightnessRequested(string monitorId, int percent)
    {
        _uiSyncContext.Post(_ =>
        {
            if (_disposed || IsDisposed) return;
            if (_contentContainer.Controls.Count == 0 ||
                _contentContainer.Controls[0] is not TableLayoutPanel table) return;
            for (int i = 0; i < table.Controls.Count; i++)
            {
                if (table.Controls[i] is MonitorRowControl row &&
                    string.Equals(row.MonitorId, monitorId, StringComparison.OrdinalIgnoreCase))
                {
                    row.UpdateBrightness(_brightnessService.ResolveReadBrightness(monitorId, percent, 0));
                    break;
                }
            }
        }, null);
    }

    private void ShowStatusState(bool loading, bool failed = false)
    {
        _contentContainer.SuspendLayout();
        try
        {
            ClearContent();

            bool isDark = IsDarkMode;

            var emptyPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = Scale(112),
                BackColor = Color.Transparent,
                Padding = new Padding(Scale(16), Scale(8), Scale(16), Scale(8)),
                TabStop = false
            };

            var glyph = new FluentLabel(_gdiCache.GlyphLargeFont)
            {
                Text = "\uE7F4", // Monitor screen glyph
                Dock = DockStyle.Top,
                Height = Scale(36),
                TextAlign = ContentAlignment.MiddleCenter,
                TabStop = false
            };
            glyph.UpdateTheme(_isMicaActive, BrightnessTray.UI.Theme.TextSecondary(isDark));

            var title = new FluentLabel(_gdiCache.BodyBoldFont)
            {
                Text = loading ? "Looking for displays…" : failed ? "Displays unavailable" : "No displays found",
                Dock = DockStyle.Top,
                Height = Scale(28),
                TextAlign = ContentAlignment.MiddleCenter,
                TabStop = false
            };
            title.UpdateTheme(_isMicaActive, BrightnessTray.UI.Theme.TextPrimary(isDark));

            var message = new FluentLabel(_gdiCache.SecondaryFont)
            {
                Text = loading ? "Your displays will appear here." : failed
                    ? "Close and reopen to try again."
                    : "Connect a display and enable DDC/CI\nin its on-screen settings.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopCenter,
                TabStop = false
            };
            message.UpdateTheme(_isMicaActive, BrightnessTray.UI.Theme.TextSecondary(isDark));
            _emptyStateGlyph = glyph;
            _emptyStateTitle = title;
            _emptyStateMessage = message;

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
        _contentContainer.AutoScrollPosition = Point.Empty;
        _contentContainer.AutoScrollMinSize = Size.Empty;
        _emptyStateGlyph = null;
        _emptyStateTitle = null;
        _emptyStateMessage = null;
        while (_contentContainer.Controls.Count > 0)
        {
            _contentContainer.Controls[0].Dispose();
        }
        _activeEditingCount = 0;
    }

    private void UpdateFormBounds(int monitorCount)
    {
        int contentHeight;

        if (monitorCount == 0)
        {
            contentHeight = Scale(112);
        }
        else
        {
            int rowsTotal = 0;

            if (_contentContainer.Controls.Count > 0 && _contentContainer.Controls[0] is TableLayoutPanel table)
            {
                for (int i = 0; i < table.Controls.Count; i++)
                {
                    if (table.Controls[i] is MonitorRowControl row)
                    {
                        rowsTotal += row.PreferredRowHeight;
                    }
                }
            }
            else
            {
                rowsTotal = monitorCount * Scale(Theme.MonitorRowHeight);
            }

            contentHeight = rowsTotal;
        }

        _desiredHeight = Padding.Vertical + contentHeight;
        PositionNearTaskbar();
    }

    public void PositionNearTaskbar()
    {
        Rectangle iconRect = Rectangle.Empty;
        bool hasIconRect = _notifyIcon != null && ShellInterop.TryGetNotifyIconRect(_notifyIcon, out iconRect);

        ShellInterop.TryGetTrayMonitorInfo(
            hasIconRect ? iconRect : null,
            Cursor.Position,
            out Rectangle workArea,
            out Rectangle bounds,
            out float monitorDpiScale);

        float dpiScale = monitorDpiScale > 0 ? monitorDpiScale : DpiScale;
        int gap = BrightnessTray.UI.Theme.FlyoutGap(dpiScale);
        // Limit the list viewport, not individual rows, so every monitor remains reachable.
        Size = new Size(Math.Min(Scale(Theme.FlyoutWidth), Math.Max(1, workArea.Width - gap * 2)),
            Math.Min(_desiredHeight, Math.Max(1, workArea.Height - gap * 2)));

        // Compensate for any invisible frame / drop shadow margin from DWM so visible edges are exact
        DwmInterop.TryGetVisibleFrameMargins(Handle, out Padding shadowMargins);

        TaskbarEdge edge = DetectTaskbarEdge(bounds, workArea, iconRect, hasIconRect, Scale(60));
        _currentTaskbarEdge = edge;

        int w = Width;
        int h = Height;

        int x;
        int y;

        switch (edge)
        {
            case TaskbarEdge.Bottom:
                // Float 12px (scaled) above the bottom taskbar, aligned 12px (scaled) from the right screen edge
                y = workArea.Bottom - h - gap + shadowMargins.Bottom;
                x = workArea.Right - w - gap + shadowMargins.Right;
                break;

            case TaskbarEdge.Top:
                // Float 12px (scaled) below the top taskbar, aligned 12px (scaled) from the right screen edge
                y = workArea.Top + gap - shadowMargins.Top;
                x = workArea.Right - w - gap + shadowMargins.Right;
                break;

            case TaskbarEdge.Left:
                // Float 12px (scaled) to the right of the left taskbar, vertically centered near tray icon
                x = workArea.Left + gap - shadowMargins.Left;
                if (hasIconRect)
                {
                    int iconCenterY = (iconRect.Top + iconRect.Bottom) / 2;
                    y = iconCenterY - (h / 2);
                }
                else
                {
                    y = workArea.Bottom - h - gap + shadowMargins.Bottom;
                }
                break;

            case TaskbarEdge.Right:
                // Float 12px (scaled) to the left of the right taskbar, vertically centered near tray icon
                x = workArea.Right - w - gap + shadowMargins.Right;
                if (hasIconRect)
                {
                    int iconCenterY = (iconRect.Top + iconRect.Bottom) / 2;
                    y = iconCenterY - (h / 2);
                }
                else
                {
                    y = workArea.Bottom - h - gap + shadowMargins.Bottom;
                }
                break;

            default:
                x = workArea.Right - w - gap + shadowMargins.Right;
                y = workArea.Bottom - h - gap + shadowMargins.Bottom;
                break;
        }

        // Clamp within the working area bounds
        int minX = workArea.Left + gap - shadowMargins.Left;
        int maxX = workArea.Right - w - gap + shadowMargins.Right;
        int minY = workArea.Top + gap - shadowMargins.Top;
        int maxY = workArea.Bottom - h - gap + shadowMargins.Bottom;

        if (maxX >= minX)
        {
            x = Math.Clamp(x, minX, maxX);
        }
        else
        {
            x = minX;
        }

        if (maxY >= minY)
        {
            y = Math.Clamp(y, minY, maxY);
        }
        else
        {
            y = minY;
        }

        Location = new Point(x, y);
    }

    private static TaskbarEdge DetectTaskbarEdge(
        Rectangle bounds,
        Rectangle workArea,
        Rectangle iconRect,
        bool hasIconRect,
        int threshold)
    {
        if (workArea.Bottom < bounds.Bottom) return TaskbarEdge.Bottom;
        if (workArea.Top > bounds.Top) return TaskbarEdge.Top;
        if (workArea.Left > bounds.Left) return TaskbarEdge.Left;
        if (workArea.Right < bounds.Right) return TaskbarEdge.Right;

        // Auto-hide taskbar: workArea == bounds. Infer from tray icon position
        if (hasIconRect)
        {
            if (iconRect.Bottom >= bounds.Bottom - threshold) return TaskbarEdge.Bottom;
            if (iconRect.Top <= bounds.Top + threshold) return TaskbarEdge.Top;
            if (iconRect.Left <= bounds.Left + threshold) return TaskbarEdge.Left;
            if (iconRect.Right >= bounds.Right - threshold) return TaskbarEdge.Right;
        }

        return TaskbarEdge.Bottom;
    }

    private enum TaskbarEdge
    {
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
                _brightnessService.BrightnessChanged -= OnBrightnessRequested;
                StopAndDisposeAnimTimer();

                ClearContent();
                _contentContainer.Dispose();
                _gdiCache.Dispose();
            }

            _disposed = true;
            _refreshCancellation?.Cancel();
        }

        base.Dispose(disposing);
    }
}
