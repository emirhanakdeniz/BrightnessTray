namespace BrightnessTray.Services;

using System.Diagnostics;
using BrightnessTray.Native;

public sealed class BrightnessService : IDisposable
{
    private readonly IBrightnessProvider _brightnessProvider;
    private readonly SettingsService _settingsService;
    private readonly Dictionary<string, MonitorDebounceState> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleLock = new();
    private Task? _shutdownTask;
    private long _brightnessVersion;
    private bool _disposed;
    internal int WorkerStartCount { get; private set; }

    private const int DebounceMs = 120;

    public event Action<string, int>? BrightnessChanged;

    private sealed class MonitorDebounceState
    {
        public Task? Worker;
        public long RequestedAt;
        public int LatestPercent;
        public long Version;
        public bool IsPending;
    }

    public BrightnessService(IBrightnessProvider brightnessProvider, SettingsService settingsService)
    {
        _brightnessProvider = brightnessProvider;
        _settingsService = settingsService;
    }

    public void RequestBrightness(string monitorId, int percent)
    {
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            return;
        }

        int clampedPercent = Math.Clamp(percent, 0, 100);
        lock (_lifecycleLock)
        {
            if (_disposed) return;

            if (!_monitors.TryGetValue(monitorId, out var state))
            {
                state = new MonitorDebounceState();
                _monitors.Add(monitorId, state);
            }
            if (state.IsPending && state.LatestPercent == clampedPercent) return;

            state.Version = ++_brightnessVersion;
            state.IsPending = true;
            state.LatestPercent = clampedPercent;
            state.RequestedAt = Stopwatch.GetTimestamp();
            if (state.Worker == null)
            {
                WorkerStartCount++;
                // Register under the same lock that shutdown uses to take its snapshot.
                state.Worker = StartWorker(monitorId, state);
            }
        }

        // Notify listeners (e.g. tooltip throttler).
        try
        {
            BrightnessChanged?.Invoke(monitorId, clampedPercent);
        }
        catch
        {
            // Fail silently
        }
    }

    internal long CaptureReadVersion()
    {
        lock (_lifecycleLock)
        {
            return _brightnessVersion;
        }
    }

    internal Task WaitForPendingWritesAsync()
    {
        lock (_lifecycleLock)
        {
            return Task.WhenAll(GetPendingWrites());
        }
    }

    internal int ResolveReadBrightness(string monitorId, int hardwarePercent, long readVersion)
    {
        lock (_lifecycleLock)
        {
            if (_monitors.TryGetValue(monitorId, out var state) &&
                (state.IsPending || state.Version > readVersion))
            {
                return state.LatestPercent;
            }

            return hardwarePercent;
        }
    }

    private Task[] GetPendingWrites() => _monitors.Values
        .Where(state => state.Worker != null).Select(state => state.Worker!).ToArray();

    // Keep the captured closure in this method so requests joining an existing
    // worker do not allocate one closure per slider movement.
    private Task StartWorker(string monitorId, MonitorDebounceState state) =>
        Task.Run(() => WriteDebouncedAsync(monitorId, state));

    internal bool HasNewerRequest(string monitorId, long readVersion)
    {
        lock (_lifecycleLock)
            return _monitors.TryGetValue(monitorId, out var state) &&
                (state.IsPending || state.Version > readVersion);
    }

    private async Task WriteDebouncedAsync(string monitorId, MonitorDebounceState state)
    {
        try
        {
            // This worker exists only while requests are pending. New requests update
            // the deadline; they do not create another task or cancellation source.
            while (true)
            {
                int targetPercent;
                long version;
                double remainingMs;
                lock (_lifecycleLock)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    remainingMs = DebounceMs - Stopwatch.GetElapsedTime(state.RequestedAt).TotalMilliseconds;
                    version = state.Version;
                    targetPercent = state.LatestPercent;
                }
                if (remainingMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(remainingMs), _cts.Token).ConfigureAwait(false);
                    continue;
                }

                // Providers own bounded retries; keep all hardware calls off the UI thread.
                WriteBrightness(monitorId, targetPercent);
                lock (_lifecycleLock)
                {
                    if (!_disposed && state.Version != version) continue;
                    CompleteWorker(state);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancels queued work, but waits for native writes already running.
        }
        catch
        {
            // Fail silently, never crash.
        }
        lock (_lifecycleLock) CompleteWorker(state);
    }

    private void CompleteWorker(MonitorDebounceState state)
    {
        state.IsPending = false;
        state.Version = ++_brightnessVersion;
        state.Worker = null;
    }

    private void WriteBrightness(string monitorId, int percent)
    {
        if (_cts.IsCancellationRequested) return;

        // Providers own their bounded hardware retries; do not multiply them here.
        try
        {
            if (_brightnessProvider.TrySetBrightness(monitorId, percent))
                _settingsService.SetBrightness(monitorId, percent);
        }
        catch { /* Keep a failed write from discarding a newer queued request. */ }
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            if (_shutdownTask != null) return _shutdownTask;

            _disposed = true;
            _cts.Cancel();
            _shutdownTask = CompleteShutdownAsync(GetPendingWrites());
            return _shutdownTask;
        }
    }

    private async Task CompleteShutdownAsync(Task[] pendingWrites)
    {
        // Native writes cannot be canceled mid-call. Keep their resources alive
        // until they return and record any successfully applied brightness.
        await Task.WhenAll(pendingWrites).ConfigureAwait(false);

        lock (_lifecycleLock) _monitors.Clear();

        _cts.Dispose();
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
