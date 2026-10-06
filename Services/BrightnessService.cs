namespace BrightnessTray.Services;

using System.Collections.Concurrent;
using BrightnessTray.Native;

public sealed class BrightnessService : IDisposable
{
    private readonly IBrightnessProvider _brightnessProvider;
    private readonly SettingsService _settingsService;
    private readonly ConcurrentDictionary<string, MonitorDebounceState> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private const int DebounceMs = 120;
    private const int MaxRetries = 2;
    private const int RetryDelayMs = 50;

    public event Action<string, int>? BrightnessChanged;

    private sealed class MonitorDebounceState : IDisposable
    {
        public readonly object Lock = new();
        public readonly SemaphoreSlim WriteGate = new(1, 1);
        public CancellationTokenSource? DebounceCts;
        public int LatestPercent;

        public void Dispose()
        {
            lock (Lock)
            {
                DebounceCts?.Cancel();
                DebounceCts?.Dispose();
                DebounceCts = null;
            }
            WriteGate.Dispose();
        }
    }

    public BrightnessService(IBrightnessProvider brightnessProvider, SettingsService settingsService)
    {
        _brightnessProvider = brightnessProvider;
        _settingsService = settingsService;
    }

    public void RequestBrightness(string monitorId, int percent)
    {
        if (_disposed || _cts.IsCancellationRequested || string.IsNullOrWhiteSpace(monitorId))
        {
            return;
        }

        int clampedPercent = Math.Clamp(percent, 0, 100);
        var state = _monitors.GetOrAdd(monitorId, _ => new MonitorDebounceState());

        CancellationToken debounceToken;

        lock (state.Lock)
        {
            state.LatestPercent = clampedPercent;
            state.DebounceCts?.Cancel();
            state.DebounceCts?.Dispose();
            state.DebounceCts = new CancellationTokenSource();
            debounceToken = state.DebounceCts.Token;
        }

        // Notify listeners (e.g. tooltip throttler)
        try
        {
            BrightnessChanged?.Invoke(monitorId, clampedPercent);
        }
        catch
        {
            // Fail silently
        }

        // Debounce ~120ms independently for this monitor on a background task
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, debounceToken).ConfigureAwait(false);

                if (_cts.IsCancellationRequested || debounceToken.IsCancellationRequested)
                {
                    return;
                }

                int targetPercent;
                lock (state.Lock)
                {
                    targetPercent = state.LatestPercent;
                }

                // Serialize writes targeting the same monitor
                await state.WriteGate.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    WriteWithRetry(monitorId, targetPercent);
                }
                finally
                {
                    state.WriteGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by newer value in burst
            }
            catch
            {
                // Fail silently, never crash
            }
        });
    }

    private void WriteWithRetry(string monitorId, int percent)
    {
        for (int attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            if (_brightnessProvider.TrySetBrightness(monitorId, percent))
            {
                // Persist the successfully written brightness value
                _settingsService.SetBrightness(monitorId, percent);
                return;
            }

            if (attempt < MaxRetries)
            {
                try
                {
                    Thread.Sleep(RetryDelayMs);
                }
                catch (ThreadInterruptedException)
                {
                    return;
                }
            }
        }
        // Silently ignore if write fails after 2 retries
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        foreach (var state in _monitors.Values)
        {
            state.Dispose();
        }
        _monitors.Clear();

        _cts.Dispose();
    }
}
