namespace BrightnessTray;

using System.Text;
using Microsoft.Win32;
using BrightnessTray.Native;
using BrightnessTray.Services;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly IBrightnessProvider _brightnessProvider;
    private readonly SettingsService _settingsService;
    private readonly BrightnessService _brightnessService;
    private readonly FlyoutForm _flyoutForm;
    private readonly TaskbarNotificationWindow _taskbarNotificationWindow;
    private readonly SynchronizationContext? _uiSyncContext;

    private readonly object _monitorStateLock = new();
    private readonly Dictionary<string, int> _monitorBrightnessMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _monitorNameMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _lastNonZeroBrightnessMap = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _systemEventDebounceCts;
    private CancellationTokenSource? _tooltipThrottleCts;
    private bool _disposed;

    public TrayApplicationContext()
    {
        _uiSyncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settingsService = new SettingsService();

        // Composite provider combining laptop internal panels (WMI) and external displays (DDC/CI)
        _brightnessProvider = new CompositeBrightnessProvider(
            new WmiProvider(),
            new DdcCiProvider()
        );

        _brightnessService = new BrightnessService(_brightnessProvider, _settingsService);
        _flyoutForm = new FlyoutForm(_brightnessProvider, _brightnessService, _settingsService);
        _flyoutForm.MonitorRenamed += OnMonitorRenamed;

        _contextMenu = new ContextMenuStrip();

        var startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = AutoStartManager.IsAutoStartEnabled()
        };
        startWithWindowsItem.Click += OnStartWithWindowsClicked;

        var listMonitorsItem = new ToolStripMenuItem("Test: list monitors", null, OnTestListMonitorsClicked);
        var setFiftyPercentItem = new ToolStripMenuItem("Test: set 50%", null, OnTestSetFiftyPercentClicked);
        var separatorItem = new ToolStripSeparator();
        var exitItem = new ToolStripMenuItem("Exit", null, OnExitClicked);

        _contextMenu.Opening += (s, e) =>
        {
            startWithWindowsItem.Checked = AutoStartManager.IsAutoStartEnabled();
        };

        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            startWithWindowsItem,
            new ToolStripSeparator(),
            listMonitorsItem,
            setFiftyPercentItem,
            separatorItem,
            exitItem
        });

        int dpi = IconRenderer.GetTaskbarDpi();
        bool isLight = IconRenderer.IsSystemTaskbarLight();

        _notifyIcon = new NotifyIcon
        {
            Text = "BrightnessTray",
            Icon = IconRenderer.CreateBrightnessIcon(isLight, dpi),
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _flyoutForm.SetNotifyIcon(_notifyIcon);
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;

        // Listen for TaskbarCreated (when explorer.exe restarts or crashes)
        _taskbarNotificationWindow = new TaskbarNotificationWindow(OnTaskbarCreated);

        // Listen for live system theme changes and display/DPI changes
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Listen for brightness adjustments to throttle tooltip updates
        _brightnessService.BrightnessChanged += OnBrightnessChanged;

        // Restore saved brightness values and initialize tooltip status
        _ = Task.Run(StartupInitializationAsync);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
        {
            DebounceSystemEvent(recreateTrayIcon: false);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        DebounceSystemEvent(recreateTrayIcon: false);
    }

    private void OnTaskbarCreated()
    {
        // When explorer.exe restarts, the tray icon needs to be recreated and re-added
        DebounceSystemEvent(recreateTrayIcon: true);
    }

    private void DebounceSystemEvent(bool recreateTrayIcon)
    {
        _systemEventDebounceCts?.Cancel();
        _systemEventDebounceCts?.Dispose();
        _systemEventDebounceCts = new CancellationTokenSource();

        var token = _systemEventDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // Debounce ~200ms to settle event bursts from Windows
                await Task.Delay(200, token).ConfigureAwait(false);

                if (token.IsCancellationRequested || _disposed)
                {
                    return;
                }

                _uiSyncContext?.Post(_ =>
                {
                    if (!_disposed)
                    {
                        RefreshTrayIcon(recreateTrayIcon);
                    }
                }, null);
            }
            catch (OperationCanceledException)
            {
                // Superseded by newer event in burst
            }
            catch
            {
                // Fail silently
            }
        });
    }

    private void RefreshTrayIcon(bool recreateTrayIcon)
    {
        try
        {
            bool isLight = IconRenderer.IsSystemTaskbarLight();
            int dpi = IconRenderer.GetTaskbarDpi();

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
        lock (_monitorStateLock)
        {
            _monitorBrightnessMap[monitorId] = percent;
        }

        ThrottleTooltipUpdate();
    }

    private void ThrottleTooltipUpdate()
    {
        _tooltipThrottleCts?.Cancel();
        _tooltipThrottleCts?.Dispose();
        _tooltipThrottleCts = new CancellationTokenSource();

        var token = _tooltipThrottleCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                // Throttle tooltip updates to ~150ms while user is dragging
                await Task.Delay(150, token).ConfigureAwait(false);

                if (token.IsCancellationRequested || _disposed)
                {
                    return;
                }

                _uiSyncContext?.Post(_ =>
                {
                    if (!_disposed)
                    {
                        ApplyTooltipText();
                    }
                }, null);
            }
            catch (OperationCanceledException)
            {
                // Throttled
            }
            catch
            {
                // Fail silently
            }
        });
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
                    singleId = _monitorBrightnessMap.Keys.First();
                    singlePercent = _monitorBrightnessMap[singleId];
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
            var monitors = await Task.Run(() => _brightnessProvider.GetMonitors());

            lock (_monitorStateLock)
            {
                _monitorBrightnessMap.Clear();
                _monitorNameMap.Clear();
                foreach (var monitor in monitors)
                {
                    _monitorBrightnessMap[monitor.Id] = monitor.CurrentPercent;
                    _monitorNameMap[monitor.Id] = monitor.Name;
                }
            }

            _uiSyncContext?.Post(_ =>
            {
                ApplyTooltipText();
                if (!_disposed)
                {
                    _flyoutForm.SeedMonitors(monitors);
                }
            }, null);

            // Restore saved brightness values
            var settings = _settingsService.GetSettings();
            if (settings.MonitorBrightness.Count > 0)
            {
                foreach (var monitor in monitors)
                {
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

    private void OnStartWithWindowsClicked(object? sender, EventArgs e)
    {
        if (sender is ToolStripMenuItem item)
        {
            AutoStartManager.SetAutoStart(item.Checked);
            item.Checked = AutoStartManager.IsAutoStartEnabled();
        }
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
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
        if (_flyoutForm.IsShowingOrVisible)
        {
            _flyoutForm.CloseFlyout();
        }
        else if (_flyoutForm.CanReopen())
        {
            _flyoutForm.ShowFlyout(_notifyIcon);
        }
    }

    private void ToggleBrightnessMute()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var monitors = await Task.Run(() => _brightnessProvider.GetMonitors());
                if (monitors.Count == 0) return;

                // Check if any monitor currently has brightness > 0
                bool anyAboveZero = monitors.Any(m => m.CurrentPercent > 0);

                foreach (var monitor in monitors)
                {
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
                    if (_flyoutForm.Visible)
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

    private async void OnTestListMonitorsClicked(object? sender, EventArgs e)
    {
        try
        {
            var monitors = await Task.Run(() => _brightnessProvider.GetMonitors());

            if (monitors.Count == 0)
            {
                MessageBox.Show(
                    "No monitors supporting WMI or DDC/CI were detected.",
                    "BrightnessTray - Monitor Detection",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Detected {monitors.Count} display(s):\n");

            foreach (var monitor in monitors)
            {
                sb.AppendLine($"• {monitor.Name}");
                sb.AppendLine($"  ID: {monitor.Id}");
                sb.AppendLine($"  Brightness: {monitor.CurrentPercent}% (raw: {monitor.CurrentBrightness}, min: {monitor.MinBrightness}, max: {monitor.MaxBrightness})");
                sb.AppendLine();
            }

            MessageBox.Show(
                sb.ToString().TrimEnd(),
                "BrightnessTray - Monitor Detection",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to enumerate monitors: {ex.Message}",
                "BrightnessTray - Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private async void OnTestSetFiftyPercentClicked(object? sender, EventArgs e)
    {
        try
        {
            var (total, successCount) = await Task.Run(() =>
            {
                var monitors = _brightnessProvider.GetMonitors();
                int success = 0;

                foreach (var monitor in monitors)
                {
                    if (_brightnessProvider.TrySetBrightness(monitor.Id, 50))
                    {
                        success++;
                    }
                }

                return (monitors.Count, success);
            });

            if (total == 0)
            {
                MessageBox.Show(
                    "No supported displays found to adjust.",
                    "BrightnessTray - Set 50%",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(
                    $"Set brightness to 50% for {successCount} of {total} display(s).",
                    "BrightnessTray - Set 50%",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to set brightness: {ex.Message}",
                "BrightnessTray - Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        ExitApplication();
    }

    private void ExitApplication()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
        }

        _flyoutForm.Hide();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

                _brightnessService.BrightnessChanged -= OnBrightnessChanged;

                _systemEventDebounceCts?.Cancel();
                _systemEventDebounceCts?.Dispose();

                _tooltipThrottleCts?.Cancel();
                _tooltipThrottleCts?.Dispose();

                _taskbarNotificationWindow.Dispose();

                if (_notifyIcon is not null)
                {
                    _notifyIcon.Visible = false;
                    var icon = _notifyIcon.Icon;
                    _notifyIcon.Dispose();
                    icon?.Dispose();
                }

                _contextMenu.Dispose();
                _flyoutForm.Dispose();
                _brightnessService.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
