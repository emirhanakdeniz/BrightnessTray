namespace BrightnessTray.Native;

using System.Diagnostics;
using System.Windows.Forms;

public sealed class SystemMessageListenerWindow : NativeWindow, IDisposable
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int PBT_APMRESUMESUSPEND = 0x0007;

    private readonly uint _taskbarCreatedMsg;
    private bool _disposed;

    // On-demand one-shot timers that exist ONLY during active debounce windows
    private System.Windows.Forms.Timer? _themeDebounceTimer;
    private System.Windows.Forms.Timer? _displayDebounceTimer;
    private System.Windows.Forms.Timer? _resumeDebounceTimer;

    private bool _pendingTaskbarRecreate;

    public event Action? ThemeOrColorChanged;
    public event Action? DisplayChanged;
    public event Action? TaskbarCreated;
    public event Action? ResumedFromSleep;

#if DEBUG
    private static int s_wakeCount;
    private static long s_lastWakeTimestamp;

    private static void LogWake(string reason)
    {
        int count = Interlocked.Increment(ref s_wakeCount);
        long now = Stopwatch.GetTimestamp();
        double elapsedSec = s_lastWakeTimestamp == 0 ? 0 : (double)(now - s_lastWakeTimestamp) / Stopwatch.Frequency;
        s_lastWakeTimestamp = now;
        Debug.WriteLine($"[IdleWatchdog] Wake #{count}: {reason} (after {elapsedSec:F2}s idle)");
    }
#endif

    public SystemMessageListenerWindow()
    {
        _taskbarCreatedMsg = TaskbarInterop.RegisterWindowMessage("TaskbarCreated");

        // Top-level invisible popup window to receive OS broadcast messages without taskbar presence
        var cp = new CreateParams
        {
            Caption = "BrightnessTray_SystemMessageListener",
            Style = unchecked((int)0x80000000) // WS_POPUP (top-level, no taskbar button, not visible)
        };

        CreateHandle(cp);

#if DEBUG
        Debug.WriteLine($"[SystemMessageListenerWindow] Created top-level message listener (HWND: 0x{Handle:X})");
#endif
    }

    protected override void WndProc(ref Message m)
    {
        if (_taskbarCreatedMsg != 0 && m.Msg == _taskbarCreatedMsg)
        {
#if DEBUG
            LogWake("TaskbarCreated broadcast received");
#endif
            ScheduleThemeAndTaskbarDebounce(recreateTaskbar: true);
            base.WndProc(ref m);
            return;
        }

        switch (m.Msg)
        {
            case WM_SETTINGCHANGE:
            {
#if DEBUG
                LogWake("WM_SETTINGCHANGE (theme/preference change)");
#endif
                ScheduleThemeAndTaskbarDebounce(recreateTaskbar: false);
                break;
            }

            case WM_DWMCOLORIZATIONCOLORCHANGED:
            {
#if DEBUG
                LogWake("WM_DWMCOLORIZATIONCOLORCHANGED (accent color change)");
#endif
                ScheduleThemeAndTaskbarDebounce(recreateTaskbar: false);
                break;
            }

            case WM_DISPLAYCHANGE:
            {
#if DEBUG
                LogWake("WM_DISPLAYCHANGE (display resolution/topology change)");
#endif
                ScheduleDisplayDebounce();
                break;
            }

            case WM_POWERBROADCAST:
            {
                long powerEvent = m.WParam.ToInt64();
                if (powerEvent == PBT_APMRESUMEAUTOMATIC || powerEvent == PBT_APMRESUMESUSPEND)
                {
#if DEBUG
                    LogWake("WM_POWERBROADCAST (system resume from sleep)");
#endif
                    ScheduleResumeDebounce();
                }
                break;
            }

        }

        base.WndProc(ref m);
    }

    private void ScheduleThemeAndTaskbarDebounce(bool recreateTaskbar)
    {
        if (_disposed) return;

        if (recreateTaskbar)
        {
            _pendingTaskbarRecreate = true;
        }

        if (_themeDebounceTimer == null)
        {
            _themeDebounceTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _themeDebounceTimer.Tick += OnThemeDebounceTick;
        }
        else
        {
            _themeDebounceTimer.Stop();
        }

        _themeDebounceTimer.Start();
    }

    private void OnThemeDebounceTick(object? sender, EventArgs e)
    {
        StopAndDisposeThemeTimer();

        if (_disposed) return;

        bool recreate = _pendingTaskbarRecreate;
        _pendingTaskbarRecreate = false;

        ThemeOrColorChanged?.Invoke();

        if (recreate)
        {
            TaskbarCreated?.Invoke();
        }
    }

    private void StopAndDisposeThemeTimer()
    {
        if (_themeDebounceTimer != null)
        {
            _themeDebounceTimer.Stop();
            _themeDebounceTimer.Tick -= OnThemeDebounceTick;
            _themeDebounceTimer.Dispose();
            _themeDebounceTimer = null;
        }
    }

    private void ScheduleDisplayDebounce()
    {
        if (_disposed) return;

        if (_displayDebounceTimer == null)
        {
            // Allow 1000ms for display drivers and DDC/CI / WMI bus to settle after hardware reconfiguration
            _displayDebounceTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _displayDebounceTimer.Tick += OnDisplayDebounceTick;
        }
        else
        {
            _displayDebounceTimer.Stop();
        }

        _displayDebounceTimer.Start();
    }

    private void OnDisplayDebounceTick(object? sender, EventArgs e)
    {
        StopAndDisposeDisplayTimer();

        if (!_disposed)
        {
            DisplayChanged?.Invoke();
        }
    }

    private void StopAndDisposeDisplayTimer()
    {
        if (_displayDebounceTimer != null)
        {
            _displayDebounceTimer.Stop();
            _displayDebounceTimer.Tick -= OnDisplayDebounceTick;
            _displayDebounceTimer.Dispose();
            _displayDebounceTimer = null;
        }
    }

    private void ScheduleResumeDebounce()
    {
        if (_disposed) return;

        if (_resumeDebounceTimer == null)
        {
            // Allow 1200ms after resume for monitors to power back on and respond to DDC/CI commands
            _resumeDebounceTimer = new System.Windows.Forms.Timer { Interval = 1200 };
            _resumeDebounceTimer.Tick += OnResumeDebounceTick;
        }
        else
        {
            _resumeDebounceTimer.Stop();
        }

        _resumeDebounceTimer.Start();
    }

    private void OnResumeDebounceTick(object? sender, EventArgs e)
    {
        StopAndDisposeResumeTimer();

        if (!_disposed)
        {
            ResumedFromSleep?.Invoke();
        }
    }

    private void StopAndDisposeResumeTimer()
    {
        if (_resumeDebounceTimer != null)
        {
            _resumeDebounceTimer.Stop();
            _resumeDebounceTimer.Tick -= OnResumeDebounceTick;
            _resumeDebounceTimer.Dispose();
            _resumeDebounceTimer = null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            StopAndDisposeThemeTimer();
            StopAndDisposeDisplayTimer();
            StopAndDisposeResumeTimer();
            DestroyHandle();
        }
    }
}
