namespace BrightnessTray;

using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using BrightnessTray.Controls;
using BrightnessTray.Native;
using BrightnessTray.Services;
using BrightnessTray.UI;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly FluentContextMenu _contextMenu;
    private readonly IBrightnessProvider _brightnessProvider;
    private readonly SettingsService _settingsService;
    private readonly BrightnessService _brightnessService;
    private readonly SystemMessageListenerWindow _systemListenerWindow;
    private readonly SynchronizationContext _uiSyncContext;

    private FlyoutForm? _flyoutForm;
    private long _lastFlyoutClosedTimestamp;

    private readonly object _monitorStateLock = new();
    private readonly Dictionary<string, int> _monitorBrightnessMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _monitorNameMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastNonZeroBrightnessMap = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<MonitorInfo> _cachedMonitors = Array.Empty<MonitorInfo>();

    private bool? _currentIconIsLight;
    private int _currentIconDpi;
    private string _lastTooltipText = "";

    // On-demand one-shot timer for tooltip updates while dragging slider
    private System.Windows.Forms.Timer? _tooltipThrottleTimer;
    private bool _disposed;
    private bool _isExiting;

#if DEBUG
    private bool _loggedIdleMessage;
#endif

    public TrayApplicationContext()
    {
        _uiSyncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settingsService = new SettingsService(_uiSyncContext);

        // Detect internal laptop displays via Win32 CCD API to avoid loading WMI/System.Management on desktop PCs
        var ddcCiProvider = new DdcCiProvider();
        _brightnessProvider = DisplayConfigInterop.HasInternalDisplay()
            ? new CompositeBrightnessProvider(new WmiProvider(), ddcCiProvider)
            : ddcCiProvider;

        _brightnessService = new BrightnessService(_brightnessProvider, _settingsService);

        _contextMenu = new FluentContextMenu(isDarkMode: true);

        var startWithWindowsItem = new FluentToggleMenuItem(
            "Start with Windows",
            "\uE7F4",
            AutoStartManager.IsAutoStartEnabled());
        startWithWindowsItem.ToggleChanged += OnStartWithWindowsToggled;

        var separatorItem = new ToolStripSeparator();
        var exitItem = new FluentMenuItem("Exit", "\uE7E8", OnExitClicked);

        _contextMenu.Opening += (s, e) =>
        {
            startWithWindowsItem.Checked = AutoStartManager.IsAutoStartEnabled();
        };

        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            startWithWindowsItem,
            separatorItem,
            exitItem
        });

        int dpi = IconRenderer.GetTaskbarDpi();
        bool isLight = IconRenderer.IsSystemTaskbarLight();
        _currentIconIsLight = isLight;
        _currentIconDpi = dpi;

        _notifyIcon = new NotifyIcon
        {
            Text = "BrightnessTray",
            Icon = IconRenderer.CreateBrightnessIcon(isLight, dpi),
            ContextMenuStrip = _contextMenu,
            Visible = true
        };
        _lastTooltipText = "BrightnessTray";

        _notifyIcon.MouseClick += OnNotifyIconMouseClick;

        // Top-level hidden native window listening for OS broadcasts (theme, display change, taskbar recreate, resume from sleep)
        _systemListenerWindow = new SystemMessageListenerWindow();
        _systemListenerWindow.ThemeOrColorChanged += OnThemeOrColorChanged;
        _systemListenerWindow.DisplayChanged += OnDisplayChanged;
        _systemListenerWindow.TaskbarCreated += OnTaskbarCreated;
        _systemListenerWindow.ResumedFromSleep += OnResumedFromSleep;

        // Listen for brightness adjustments to throttle tooltip updates
        _brightnessService.BrightnessChanged += OnBrightnessChanged;

        // Listen for live system theme changes (Rule 5)
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Restore saved brightness values and initialize tooltip status
        _ = Task.Run(StartupInitializationAsync);

#if DEBUG
        Application.Idle += (s, e) =>
        {
            if (!_loggedIdleMessage)
            {
                _loggedIdleMessage = true;
                Debug.WriteLine("[IdleWatchdog] Message loop drained. Main thread is blocked waiting for OS events.");
            }
        };
#endif
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.VisualStyle)
        {
            OnThemeOrColorChanged();
        }
    }

    private void OnThemeOrColorChanged()
    {
        if (_disposed || _isExiting) return;
#if DEBUG
        _loggedIdleMessage = false;
#endif
        RefreshTrayIcon(recreateTrayIcon: false);
        Color accent = DwmInterop.GetAccentColor();
        _contextMenu.UpdateTheme(isDark: true, accent);

        if (_flyoutForm != null && !_flyoutForm.IsDisposed)
        {
            _flyoutForm.OnThemeOrColorChanged();
        }
    }

    private void OnDisplayChanged()
    {
        if (_disposed || _isExiting) return;
#if DEBUG
        _loggedIdleMessage = false;
#endif
        RefreshTrayIcon(recreateTrayIcon: false);
        if (_flyoutForm != null && !_flyoutForm.IsDisposed)
        {
            _flyoutForm.OnDisplayConfigurationChanged();
        }
        _ = Task.Run(StartupInitializationAsync);
    }

    private void OnTaskbarCreated()
    {
        if (_disposed || _isExiting) return;
#if DEBUG
        _loggedIdleMessage = false;
#endif
        RefreshTrayIcon(recreateTrayIcon: true);
    }

    private void OnResumedFromSleep()
    {
        if (_disposed || _isExiting) return;
#if DEBUG
        _loggedIdleMessage = false;
#endif
        _ = Task.Run(StartupInitializationAsync);
    }

    private void RefreshTrayIcon(bool recreateTrayIcon)
    {
        try
        {
            bool isLight = IconRenderer.IsSystemTaskbarLight();
            int dpi = IconRenderer.GetTaskbarDpi();

            if (!recreateTrayIcon && _currentIconIsLight == isLight && _currentIconDpi == dpi && _notifyIcon.Icon != null)
            {
                return;
            }

            _currentIconIsLight = isLight;
            _currentIconDpi = dpi;

            var oldIcon = _notifyIcon.Icon;
            _notifyIcon.Icon = IconRenderer.CreateBrightnessIcon(isLight, dpi);
            oldIcon?.Dispose();

            if (recreateTrayIcon)
            {
                // Toggle visibility to re-register icon with newly created taskbar
                _notifyIcon.Visible = false;
                _notifyIcon.Visible = true;
            }
        }
        catch
        {
            // Fail silently
        }
    }

    private void OnBrightnessChanged(string monitorId, int percent)
    {
        _uiSyncContext.Post(_ =>
        {
            if (_disposed || _isExiting) return;
            lock (_monitorStateLock)
            {
                _monitorBrightnessMap[monitorId] = percent;
            }

            ThrottleTooltipUpdate();
        }, null);
    }

    private void ThrottleTooltipUpdate()
    {
        if (_disposed) return;

        if (_tooltipThrottleTimer == null)
        {
            _tooltipThrottleTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _tooltipThrottleTimer.Tick += OnTooltipThrottleTick;
        }
        else
        {
            _tooltipThrottleTimer.Stop();
        }

        _tooltipThrottleTimer.Start();
    }

    private void OnTooltipThrottleTick(object? sender, EventArgs e)
    {
        StopAndDisposeTooltipTimer();
        if (!_disposed)
        {
            ApplyTooltipText();
        }
    }

    private void StopAndDisposeTooltipTimer()
    {
        if (_tooltipThrottleTimer != null)
        {
            _tooltipThrottleTimer.Stop();
            _tooltipThrottleTimer.Tick -= OnTooltipThrottleTick;
            _tooltipThrottleTimer.Dispose();
            _tooltipThrottleTimer = null;
        }
    }

    private void OnMonitorRenamed(string monitorId, string newDisplayName)
    {
        lock (_monitorStateLock)
        {
            _monitorNameMap[monitorId] = newDisplayName;
        }
        ApplyTooltipText();
    }

    private void ApplyTooltipText()
    {
        try
        {
            int count;
            int singlePercent = 0;
            string singleId = "";
            string? singleName = null;

            lock (_monitorStateLock)
            {
                count = _monitorBrightnessMap.Count;
                if (count == 1)
                {
                    foreach (var pair in _monitorBrightnessMap)
                    {
                        singleId = pair.Key;
                        singlePercent = pair.Value;
                        break;
                    }
                    _monitorNameMap.TryGetValue(singleId, out singleName);
                }
            }

            string text;
            if (count == 0)
            {
                text = "BrightnessTray";
            }
            else if (count == 1)
            {
                string resolvedName = MonitorNameResolver.Resolve(singleId, singleName ?? "", 1, _settingsService);
                text = $"{resolvedName}: {singlePercent}%";
            }
            else
            {
                text = $"Brightness: {count} displays";
            }

            // Clamp strictly to 127 characters limit
            if (text.Length > 127)
            {
                text = text[..127];
            }

            if (string.Equals(_lastTooltipText, text, StringComparison.Ordinal))
            {
                return;
            }

            _lastTooltipText = text;
            _notifyIcon.Text = text;
        }
        catch
        {
            // Fail silently
        }
    }

    private async Task StartupInitializationAsync()
    {
        try
        {
            if (_disposed || _isExiting) return;
            var monitors = await Task.Run(() => _brightnessProvider.GetMonitors());

            lock (_monitorStateLock)
            {
                _cachedMonitors = monitors;
                _monitorBrightnessMap.Clear();
                _monitorNameMap.Clear();
                for (int i = 0; i < monitors.Count; i++)
                {
                    var m = monitors[i];
                    _monitorBrightnessMap[m.Id] = m.CurrentPercent;
                    _monitorNameMap[m.Id] = m.Name;
                }
            }

            _uiSyncContext?.Post(_ =>
            {
                if (_disposed || _isExiting) return;
                ApplyTooltipText();
                if (!_disposed && _flyoutForm != null && !_flyoutForm.IsDisposed)
                {
                    _flyoutForm.SeedMonitors(monitors);
                }
            }, null);

            // Restore saved brightness values
            var settings = _settingsService.GetSettings();
            if (settings.MonitorBrightness.Count > 0)
            {
                for (int i = 0; i < monitors.Count; i++)
                {
                    var monitor = monitors[i];
                    if (settings.MonitorBrightness.TryGetValue(monitor.Id, out int savedPercent))
                    {
                        if (savedPercent != monitor.CurrentPercent)
                        {
                            _brightnessService.RequestBrightness(monitor.Id, savedPercent);
                        }
                    }
                }
            }
        }
        catch
        {
            // Fail silently
        }
    }

    private void OnStartWithWindowsToggled(object? sender, bool isChecked)
    {
        AutoStartManager.SetAutoStart(isChecked);
        if (sender is FluentToggleMenuItem item)
        {
            item.Checked = AutoStartManager.IsAutoStartEnabled();
        }
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
#if DEBUG
        _loggedIdleMessage = false;
#endif
        if (e.Button == MouseButtons.Left)
        {
            ToggleFlyout();
        }
        else if (e.Button == MouseButtons.Middle)
        {
            ToggleBrightnessMute();
        }
    }

    private void ToggleFlyout()
    {
        if (_disposed || _isExiting) return;

        if (_flyoutForm != null && _flyoutForm.IsShowingOrVisible)
        {
            _flyoutForm.CloseFlyout();
            return;
        }

        if (_flyoutForm != null && !_flyoutForm.CanReopen())
        {
            return;
        }

        if (_lastFlyoutClosedTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastFlyoutClosedTimestamp).TotalMilliseconds < 200)
        {
            return;
        }

        EnsureFlyoutFormCreated();
        _flyoutForm!.ShowFlyout(_notifyIcon);
    }

    private void EnsureFlyoutFormCreated()
    {
        if (_flyoutForm == null || _flyoutForm.IsDisposed)
        {
            long start = Stopwatch.GetTimestamp();
            _flyoutForm = new FlyoutForm(_brightnessProvider, _brightnessService, _settingsService);
            _flyoutForm.SetNotifyIcon(_notifyIcon);
            _flyoutForm.MonitorRenamed += OnMonitorRenamed;
            _flyoutForm.FlyoutClosed += OnFlyoutClosed;

            IReadOnlyList<MonitorInfo> monitorsToSeed;
            lock (_monitorStateLock)
            {
                monitorsToSeed = _cachedMonitors;
            }

            if (monitorsToSeed.Count > 0)
            {
                _flyoutForm.SeedMonitors(monitorsToSeed);
            }

            long elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency;
            Debug.WriteLine($"[FlyoutLifecycle] Created FlyoutForm in {elapsedMs} ms");
        }
    }

    private void OnFlyoutClosed()
    {
        if (_disposed || _isExiting) return;
        _lastFlyoutClosedTimestamp = Stopwatch.GetTimestamp();
        _settingsService.Flush();
        var closedFlyout = _flyoutForm;
        // Dispose after the close event returns, without a hidden retention timer.
        _uiSyncContext.Post(_ =>
        {
            if (ReferenceEquals(_flyoutForm, closedFlyout)) DisposeClosedFlyout();
        }, null);
    }

    private void DisposeClosedFlyout()
    {
        if (_disposed) return;

        if (_flyoutForm != null && !_flyoutForm.IsShowingOrVisible)
        {
            _flyoutForm.MonitorRenamed -= OnMonitorRenamed;
            _flyoutForm.FlyoutClosed -= OnFlyoutClosed;
            _flyoutForm.Dispose();
            _flyoutForm = null;

#if DEBUG
            Debug.WriteLine("[FlyoutLifecycle] Closed FlyoutForm disposed.");
#endif
        }
    }

    private void ToggleBrightnessMute()
    {
        if (_disposed || _isExiting) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var monitors = await Task.Run(() => _brightnessProvider.GetMonitors());
                if (monitors.Count == 0) return;

                // Check if any monitor currently has brightness > 0
                bool anyAboveZero = false;
                for (int i = 0; i < monitors.Count; i++)
                {
                    if (monitors[i].CurrentPercent > 0)
                    {
                        anyAboveZero = true;
                        break;
                    }
                }

                for (int i = 0; i < monitors.Count; i++)
                {
                    var monitor = monitors[i];
                    int targetPercent;
                    if (anyAboveZero)
                    {
                        // Save previous brightness before muting to 0%
                        if (monitor.CurrentPercent > 0)
                        {
                            _lastNonZeroBrightnessMap[monitor.Id] = monitor.CurrentPercent;
                        }
                        targetPercent = 0;
                    }
                    else
                    {
                        // Restore from last recorded level or default to 50%
                        if (!_lastNonZeroBrightnessMap.TryGetValue(monitor.Id, out targetPercent) || targetPercent <= 0)
                        {
                            targetPercent = 50;
                        }
                    }

                    _brightnessService.RequestBrightness(monitor.Id, targetPercent);
                }

                // Update flyout controls if currently open
                _uiSyncContext?.Post(_ =>
                {
                    if (_disposed || _isExiting) return;
                    if (_flyoutForm != null && !_flyoutForm.IsDisposed && _flyoutForm.Visible)
                    {
                        _ = _flyoutForm.RefreshMonitorsAsync();
                    }
                }, null);
            }
            catch
            {
                // Fail silently
            }
        });
    }

    private async void OnExitClicked(object? sender, EventArgs e)
    {
        await ExitApplicationAsync();
    }

    private async Task ExitApplicationAsync()
    {
        if (_disposed || _isExiting) return;
        _isExiting = true;
        StopAndDisposeTooltipTimer();

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
        }

        _flyoutForm?.Hide();
        await _brightnessService.StopAsync();
        _settingsService.Dispose();
        ExitThread();
    }

    internal void RunLifecycleTest()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Memory & Object Lifetime Diagnostic ===");
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        sb.AppendLine("Baseline Idle (Flyout Not Created):");
        sb.AppendLine($"  Working Set:    {proc.WorkingSet64 / 1024.0 / 1024.0:F2} MB");
        sb.AppendLine($"  Private Memory: {proc.PrivateMemorySize64 / 1024.0 / 1024.0:F2} MB");

        // 1. First flyout creation & open
        var swCreate = Stopwatch.StartNew();
        EnsureFlyoutFormCreated();
        swCreate.Stop();

        var swShow = Stopwatch.StartNew();
        _flyoutForm!.ShowFlyout(_notifyIcon);
        Application.DoEvents();
        swShow.Stop();

        long firstCreateMs = swCreate.ElapsedMilliseconds;
        long firstShowMs = swShow.ElapsedMilliseconds;
        long totalFirstOpenMs = firstCreateMs + firstShowMs;

        proc.Refresh();
        sb.AppendLine($"\nFlyout Opened (Create: {firstCreateMs} ms, First Frame: {firstShowMs} ms, Total: {totalFirstOpenMs} ms):");
        sb.AppendLine($"  Working Set:    {proc.WorkingSet64 / 1024.0 / 1024.0:F2} MB");
        sb.AppendLine($"  Private Memory: {proc.PrivateMemorySize64 / 1024.0 / 1024.0:F2} MB");

        // 2. Close flyout
        _flyoutForm.CloseFlyout();
        for (int i = 0; i < 20; i++)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        // 3. Drain the queued disposal after the close animation.
        DisposeClosedFlyout();
        Application.DoEvents();

        proc.Refresh();
        sb.AppendLine($"\nAfter Flyout Disposed (without forced collection or working-set trimming):");
        sb.AppendLine($"  Working Set:    {proc.WorkingSet64 / 1024.0 / 1024.0:F2} MB");
        sb.AppendLine($"  Private Memory: {proc.PrivateMemorySize64 / 1024.0 / 1024.0:F2} MB");

        // 4. Reopen after disposal (cold creation from cache)
        var swReopenCreate = Stopwatch.StartNew();
        EnsureFlyoutFormCreated();
        swReopenCreate.Stop();

        var swReopenShow = Stopwatch.StartNew();
        _flyoutForm!.ShowFlyout(_notifyIcon);
        Application.DoEvents();
        swReopenShow.Stop();

        long reopenCreateMs = swReopenCreate.ElapsedMilliseconds;
        long reopenShowMs = swReopenShow.ElapsedMilliseconds;
        long totalReopenMs = reopenCreateMs + reopenShowMs;

        proc.Refresh();
        sb.AppendLine($"\nReopened from Teardown (Create: {reopenCreateMs} ms, First Frame: {reopenShowMs} ms, Total: {totalReopenMs} ms):");
        sb.AppendLine($"  Working Set:    {proc.WorkingSet64 / 1024.0 / 1024.0:F2} MB");
        sb.AppendLine($"  Private Memory: {proc.PrivateMemorySize64 / 1024.0 / 1024.0:F2} MB");

        _flyoutForm.CloseFlyout();
        for (int i = 0; i < 20; i++)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        DisposeClosedFlyout();
        Application.DoEvents();
        sb.AppendLine("============================================");

        File.WriteAllText("lifecycle_results.txt", sb.ToString());
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _brightnessService.BrightnessChanged -= OnBrightnessChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

                StopAndDisposeTooltipTimer();
                _systemListenerWindow.Dispose();

                if (_notifyIcon is not null)
                {
                    _notifyIcon.Visible = false;
                    var icon = _notifyIcon.Icon;
                    _notifyIcon.Dispose();
                    icon?.Dispose();
                }

                _contextMenu.Dispose();

                if (_flyoutForm != null)
                {
                    _flyoutForm.MonitorRenamed -= OnMonitorRenamed;
                    _flyoutForm.FlyoutClosed -= OnFlyoutClosed;
                    _flyoutForm.Dispose();
                    _flyoutForm = null;
                }

                _brightnessService.Dispose();
                _settingsService.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
