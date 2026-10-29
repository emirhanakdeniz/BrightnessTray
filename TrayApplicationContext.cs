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
    private FluentContextMenu? _contextMenu;
    private readonly IBrightnessProvider _brightnessProvider;
    private readonly SettingsService _settingsService;
    private readonly BrightnessService _brightnessService;
    private readonly MonitorDiscoveryService _monitorDiscovery;
    private readonly TrayWheelSelection _trayWheelSelection = new();
    private readonly TrayWheelListener _trayWheelListener;
    private Task? _startupInitializationTask;
    private readonly SystemMessageListenerWindow _systemListenerWindow;
    private readonly SynchronizationContext _uiSyncContext;

    private FlyoutForm? _flyoutForm;
    private AboutForm? _aboutForm;
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
    private bool _isTogglingBrightnessMute;

#if DEBUG
    private bool _loggedIdleMessage;
#endif

    public TrayApplicationContext()
        : this(CreateBrightnessProvider(), new SettingsService())
    {
    }

    internal TrayApplicationContext(IBrightnessProvider brightnessProvider, SettingsService settingsService)
    {
        _uiSyncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settingsService = settingsService;
        _brightnessProvider = brightnessProvider;

        _brightnessService = new BrightnessService(_brightnessProvider, _settingsService);
        _monitorDiscovery = new MonitorDiscoveryService(_brightnessProvider, _brightnessService);

        int dpi = IconRenderer.GetTaskbarDpi();
        bool isLight = IconRenderer.IsSystemTaskbarLight();
        _currentIconIsLight = isLight;
        _currentIconDpi = dpi;

        _notifyIcon = new NotifyIcon
        {
            Text = "BrightnessTray",
            Icon = IconRenderer.CreateBrightnessIcon(isLight, dpi),
            Visible = true
        };
        _lastTooltipText = "BrightnessTray";

        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
        _notifyIcon.MouseDown += OnNotifyIconMouseDown;
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
        _trayWheelListener = new TrayWheelListener(_notifyIcon, OnTrayMouseWheel, _trayWheelSelection.Reset);

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
        _ = StartupInitializationAsync();

#if DEBUG
        Application.Idle += OnApplicationIdle;
#endif
    }

    private static IBrightnessProvider CreateBrightnessProvider()
    {
        // Avoid loading WMI/System.Management on desktop PCs.
        var ddcCiProvider = new DdcCiProvider();
        return DisplayConfigInterop.HasInternalDisplay()
            ? new CompositeBrightnessProvider(new WmiProvider(), ddcCiProvider)
            : ddcCiProvider;
    }

#if DEBUG
    private void OnApplicationIdle(object? sender, EventArgs e)
    {
        if (_loggedIdleMessage) return;
        _loggedIdleMessage = true;
        Debug.WriteLine("[IdleWatchdog] Message loop drained. Main thread is blocked waiting for OS events.");
    }
#endif

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.VisualStyle)
        {
            _uiSyncContext.Post(_ => OnThemeOrColorChanged(), null);
        }
    }

    private void OnThemeOrColorChanged()
    {
        if (_disposed || _isExiting) return;
#if DEBUG
        _loggedIdleMessage = false;
#endif
        RefreshTrayIcon(recreateTrayIcon: false);
        _contextMenu?.UpdateTheme(isDark: !DwmInterop.IsAppsUseLightTheme(), DwmInterop.GetAccentColor());
        _aboutForm?.UpdateTheme();

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
        _monitorDiscovery.Invalidate();
        InvalidateTrayWheelMonitors();
        _ = StartupInitializationAsync();
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
        _monitorDiscovery.Invalidate();
        InvalidateTrayWheelMonitors();
        _ = StartupInitializationAsync();
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
                lock (_monitorStateLock)
                {
                    var target = TrayWheelSelection.ResolveTarget(_cachedMonitors, _settingsService.GetTrayWheelMonitorId());
                    if (target != null && _monitorBrightnessMap.TryGetValue(target.Id, out int percent))
                    {
                        int index = 0;
                        while (index < _cachedMonitors.Count && !ReferenceEquals(_cachedMonitors[index], target)) index++;
                        string name = MonitorNameResolver.Resolve(target.Id, target.Name,
                            index + 1, _settingsService);
                        text = $"Scroll: {name}: {percent}%";
                    }
                    else
                    {
                        text = $"Brightness: {count} displays — right-click to choose wheel display";
                    }
                }
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

    private Task StartupInitializationAsync()
    {
        if (_startupInitializationTask is { IsCompleted: false }) return _startupInitializationTask;
        return _startupInitializationTask = InitializeStartupAsync();
    }

    private async Task InitializeStartupAsync()
    {
        try
        {
            if (_disposed || _isExiting) return;
            MonitorDiscoveryService.Snapshot snapshot;
            do
            {
                snapshot = await _monitorDiscovery.GetMonitorsAsync();
                if (_disposed || _isExiting) return;
            } while (!_monitorDiscovery.IsCurrent(snapshot));
            var monitors = snapshot.Monitors;
            if (_disposed || _isExiting) return;

            lock (_monitorStateLock)
            {
                _cachedMonitors = monitors;
                _monitorBrightnessMap.Clear();
                _monitorNameMap.Clear();
                for (int i = 0; i < monitors.Count; i++)
                {
                    var m = monitors[i];
                    _monitorBrightnessMap[m.Id] = _brightnessService.ResolveReadBrightness(m.Id, m.CurrentPercent, snapshot.ReadVersion);
                    _monitorNameMap[m.Id] = m.Name;
                }
            }

            _uiSyncContext.Post(_ =>
            {
                if (_disposed || _isExiting || !_monitorDiscovery.IsCurrent(snapshot)) return;
                ApplyTooltipText();
                if (_flyoutForm != null && !_flyoutForm.IsDisposed)
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
                        if (savedPercent != monitor.CurrentPercent &&
                            !_brightnessService.HasNewerRequest(monitor.Id, snapshot.ReadVersion))
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

    private void OnNotifyIconMouseDown(object? sender, MouseEventArgs e)
    {
        // NotifyIcon displays its menu before raising MouseUp. Attach it on
        // right-button down so WinForms retains native tray focus/dismissal.
        if (e.Button == MouseButtons.Right) EnsureContextMenuCreated();
    }

    private void InvalidateTrayWheelMonitors()
    {
        lock (_monitorStateLock) _cachedMonitors = Array.Empty<MonitorInfo>();
        _trayWheelSelection.Reset();
    }

    private bool OnTrayMouseWheel(int delta)
    {
        if (_disposed || _isExiting || _contextMenu is { Visible: true }) return false;
        lock (_monitorStateLock)
        {
            var target = TrayWheelSelection.ResolveTarget(_cachedMonitors, _settingsService.GetTrayWheelMonitorId());
            if (target == null) return false;
            int baseline = _monitorBrightnessMap.GetValueOrDefault(target.Id, target.CurrentPercent);
            int current = _brightnessService.ResolveReadBrightness(target.Id, baseline, long.MaxValue);
            int next = _trayWheelSelection.GetBrightness(target.Id, current, delta);
            if (next != current)
            {
                _monitorBrightnessMap[target.Id] = next;
                _brightnessService.RequestSmoothBrightness(target.Id, target.CurrentPercent, next);
            }
            return true; // Consume partial deltas too, but only with a valid target.
        }
    }

    private FluentMenuItem CreateTrayWheelMenu()
    {
        var picker = new FluentMenuItem("Mouse wheel display", "\uE7F4");
        string? selectedId = _settingsService.GetTrayWheelMonitorId();
        lock (_monitorStateLock)
        {
            var target = TrayWheelSelection.ResolveTarget(_cachedMonitors, selectedId);
            string hint = _cachedMonitors.Count switch
            {
                0 => "No controllable displays",
                1 => "One display: selected automatically",
                _ when target != null => "Scroll over the tray icon to adjust",
                _ when selectedId != null => "Selected display is disconnected",
                _ => "Choose a display for scrolling"
            };
            picker.DropDownItems.Add(new FluentMenuItem(hint) { Enabled = false });
            if (_cachedMonitors.Count > 0) picker.DropDownItems.Add(new ToolStripSeparator());
            for (int i = 0; i < _cachedMonitors.Count; i++)
            {
                var monitor = _cachedMonitors[i];
                string name = MonitorNameResolver.Resolve(monitor.Id, monitor.Name, i + 1, _settingsService);
                var item = new FluentMenuItem($"{i + 1}. {name}", onClick: (_, _) =>
                {
                    _settingsService.SetTrayWheelMonitorId(monitor.Id);
                    _trayWheelSelection.Reset();
                    ApplyTooltipText();
                })
                {
                    Checked = string.Equals(target?.Id, monitor.Id, StringComparison.OrdinalIgnoreCase)
                };
                picker.DropDownItems.Add(item);
            }
        }
        return picker;
    }

    private void OnNotifyIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && _contextMenu is { Visible: false })
            DisposeContextMenu();
    }

    private void EnsureContextMenuCreated()
    {
        if (_disposed || _isExiting || _contextMenu != null) return;

        var menu = new FluentContextMenu();
        var startupItem = new FluentToggleMenuItem(
            "Start with Windows", "\uE7F4", AutoStartManager.IsAutoStartEnabled());
        startupItem.ToggleChanged += OnStartWithWindowsToggled;
        menu.Opening += (_, _) => startupItem.Checked = AutoStartManager.IsAutoStartEnabled();
        menu.Items.AddRange([
            startupItem,
            CreateTrayWheelMenu(),
            new ToolStripSeparator(),
            new FluentMenuItem("About BrightnessTray", "\uE946", OnAboutClicked),
            new FluentMenuItem("Exit", "\uE7E8", OnExitClicked)
        ]);
        menu.UpdateTheme(!DwmInterop.IsAppsUseLightTheme(), DwmInterop.GetAccentColor());
        menu.Closed += (_, _) =>
        {
            // Let item clicks (including Exit) finish before disposing items.
            _uiSyncContext.Post(_ =>
            {
                if (ReferenceEquals(_contextMenu, menu) && !menu.Visible)
                    DisposeContextMenu();
            }, null);
        };
        _contextMenu = menu;
        _notifyIcon.ContextMenuStrip = menu;
    }

    private void OnAboutClicked(object? sender, EventArgs e)
    {
        if (_disposed || _isExiting) return;
        if (_aboutForm != null && !_aboutForm.IsDisposed)
        {
            _aboutForm.Activate();
            return;
        }

        var aboutForm = new AboutForm();
        aboutForm.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_aboutForm, aboutForm)) _aboutForm = null;
        };
        _aboutForm = aboutForm;
        aboutForm.Show();
    }

    private void DisposeContextMenu()
    {
        _notifyIcon.ContextMenuStrip = null;
        _contextMenu?.Dispose();
        _contextMenu = null;
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
            _ = ToggleBrightnessMuteAsync();
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
            _flyoutForm = new FlyoutForm(_brightnessProvider, _brightnessService, _settingsService,
                monitorDiscovery: _monitorDiscovery);
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

    private async void OnFlyoutClosed()
    {
        if (_disposed || _isExiting) return;
        _lastFlyoutClosedTimestamp = Stopwatch.GetTimestamp();
        StopAndDisposeTooltipTimer();
        ApplyTooltipText();
        _settingsService.Flush();
        var closedFlyout = _flyoutForm;
        // Dispose after the close event returns, without a hidden retention timer.
        _uiSyncContext.Post(_ =>
        {
            if (ReferenceEquals(_flyoutForm, closedFlyout)) DisposeClosedFlyout();
        }, null);

        // Closing the UI must not cancel the user's last brightness request.
        // Flush again after late successful writes, then release the save timer.
        await _brightnessService.WaitForPendingWritesAsync();
        if (!_disposed && !_isExiting && _flyoutForm is not { IsShowingOrVisible: true })
            _settingsService.Flush();
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

    private async Task ToggleBrightnessMuteAsync()
    {
        if (_disposed || _isExiting || _isTogglingBrightnessMute) return;
        _isTogglingBrightnessMute = true;
        try
        {
            MonitorDiscoveryService.Snapshot snapshot;
            do
            {
                snapshot = await _monitorDiscovery.GetMonitorsAsync();
                if (_disposed || _isExiting) return;
            } while (!_monitorDiscovery.IsCurrent(snapshot));
            long readVersion = snapshot.ReadVersion;
            var monitors = snapshot.Monitors;
            if (_disposed || _isExiting || monitors.Count == 0) return;

            // Keep state on the UI thread and include writes still in the debounce window.
            var currentPercents = new int[monitors.Count];
            bool anyAboveZero = false;
            for (int i = 0; i < monitors.Count; i++)
            {
                currentPercents[i] = _brightnessService.ResolveReadBrightness(
                    monitors[i].Id, monitors[i].CurrentPercent, readVersion);
                anyAboveZero |= currentPercents[i] > 0;
            }

            for (int i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i];
                int targetPercent;
                if (anyAboveZero)
                {
                    if (currentPercents[i] > 0)
                        _lastNonZeroBrightnessMap[monitor.Id] = currentPercents[i];
                    targetPercent = 0;
                }
                else if (!_lastNonZeroBrightnessMap.TryGetValue(monitor.Id, out targetPercent) || targetPercent <= 0)
                {
                    targetPercent = 50;
                }

                // BrightnessChanged already updates both the tooltip and any open flyout.
                _brightnessService.RequestBrightness(monitor.Id, targetPercent);
            }
        }
        catch
        {
            // Hardware may disappear during discovery.
        }
        finally
        {
            _isTogglingBrightnessMute = false;
        }
    }

    private async void OnExitClicked(object? sender, EventArgs e)
    {
        await ExitApplicationAsync();
    }

    private async Task ExitApplicationAsync()
    {
        if (_disposed || _isExiting) return;
        _isExiting = true;
        _trayWheelListener.Dispose();
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
        using var proc = Process.GetCurrentProcess();
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

    internal void RunPerformanceTest()
    {
        var report = new StringBuilder();
        using var process = Process.GetCurrentProcess();
        void Record(string label)
        {
            process.Refresh();
            report.AppendLine($"{label}:");
            report.AppendLine($"  Working Set: {process.WorkingSet64 / 1024.0 / 1024.0:F2} MiB");
            report.AppendLine($"  Private Memory: {process.PrivateMemorySize64 / 1024.0 / 1024.0:F2} MiB");
            report.AppendLine($"  Managed Heap: {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F2} MiB");
            report.AppendLine($"  Handles: {process.HandleCount}, GDI: {MemoryInterop.GetGuiResources(process.Handle, 0)}, USER: {MemoryInterop.GetGuiResources(process.Handle, 1)}");
        }

        report.AppendLine("=== 100 Flyout Cycles & 60 Seconds Idle ===");
        Record("Before cycles (after lifecycle warmup)");
        for (int cycle = 0; cycle < 100; cycle++)
        {
            EnsureFlyoutFormCreated();
            _flyoutForm!.ShowFlyout(_notifyIcon);
            Application.DoEvents();
            _flyoutForm.CloseFlyout();
            for (int tick = 0; tick < 20; tick++)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            DisposeClosedFlyout();
            Application.DoEvents();
            if ((cycle + 1) % 25 == 0) Record($"After {cycle + 1} cycles");
        }
        Record("After 100 cycles (no forced GC or working-set trim)");

        // Diagnostic-only one-shot timers. A real message loop keeps broadcasts
        // and queued callbacks running during the idle sample.
        using var loop = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 5000 };
        TimeSpan cpuStart = default;
        long sampleStarted = 0;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (sampleStarted == 0)
            {
                Record("Settled idle start");
                cpuStart = process.TotalProcessorTime;
                sampleStarted = Stopwatch.GetTimestamp();
                timer.Interval = 60000;
                timer.Start();
                return;
            }

            Record("Settled idle end");
            double elapsedSeconds = Stopwatch.GetElapsedTime(sampleStarted).TotalSeconds;
            double cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
            report.AppendLine($"Idle sample: {elapsedSeconds:F2} s, CPU time: {cpuMs:F2} ms");
            File.WriteAllText("performance_results.txt", report.ToString());
            loop.ExitThread();
        };
        timer.Start();
        Application.Run(loop);
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
#if DEBUG
                Application.Idle -= OnApplicationIdle;
#endif

                StopAndDisposeTooltipTimer();
                _trayWheelListener.Dispose();
                _systemListenerWindow.Dispose();

                if (_notifyIcon is not null)
                {
                    _notifyIcon.Visible = false;
                    var icon = _notifyIcon.Icon;
                    _notifyIcon.Dispose();
                    icon?.Dispose();
                }

                DisposeContextMenu();

                _aboutForm?.Dispose();
                _aboutForm = null;

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
