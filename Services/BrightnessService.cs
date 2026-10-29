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
    private const int SmoothWriteIntervalMs = 40;
    private const int SmoothDurationMs = 160;

    public event Action<string, int>? BrightnessChanged;

    private sealed class MonitorDebounceState
    {
        public Task? Worker;
        public long RequestedAt;
        public int LatestPercent;
        public long Version;
        public bool IsPending;
        public bool Smooth;
        public bool HasAppliedPercent;
        public int AppliedPercent;
        public int SmoothFromPercent;
        public long SmoothStartedAt;
        public long LastWriteStartedAt;
    }

    public BrightnessService(IBrightnessProvider brightnessProvider, SettingsService settingsService)
    {
        _brightnessProvider = brightnessProvider;
        _settingsService = settingsService;
    }

    public void RequestBrightness(string monitorId, int percent)
    {
        RequestBrightnessCore(monitorId, percent, null);
    }

    internal void RequestSmoothBrightness(string monitorId, int currentPercent, int percent)
    {
        RequestBrightnessCore(monitorId, percent, Math.Clamp(currentPercent, 0, 100));
    }

    private void RequestBrightnessCore(string monitorId, int percent, int? hardwarePercent)
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
            bool smooth = hardwarePercent.HasValue;
            if (state.IsPending && state.LatestPercent == clampedPercent && state.Smooth == smooth) return;

            state.Version = ++_brightnessVersion;
            state.IsPending = true;
            state.LatestPercent = clampedPercent;
            state.RequestedAt = Stopwatch.GetTimestamp();
            state.Smooth = smooth;
            if (smooth)
            {
                // Prefer the last successful write over an older discovery snapshot.
                if (!state.HasAppliedPercent)
                {
                    state.AppliedPercent = hardwarePercent!.Value;
                    state.HasAppliedPercent = true;
                }
                state.SmoothFromPercent = state.AppliedPercent;
                state.SmoothStartedAt = state.RequestedAt;
            }
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
            // One worker handles both debounced slider requests and paced wheel
            // transitions. Retargeting never creates another worker or retry layer.
            while (true)
            {
                int targetPercent;
                long version;
                double remainingMs;
                lock (_lifecycleLock)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    version = state.Version;
                    targetPercent = state.LatestPercent;
                    if (state.Smooth)
                    {
                        if (state.AppliedPercent == targetPercent)
                        {
                            CompleteWorker(state);
                            return;
                        }
                        remainingMs = state.LastWriteStartedAt == 0 ? 0 :
                            SmoothWriteIntervalMs - Stopwatch.GetElapsedTime(state.LastWriteStartedAt).TotalMilliseconds;
                        targetPercent = NextSmoothPercent(state);
                    }
                    else
                    {
                        remainingMs = DebounceMs - Stopwatch.GetElapsedTime(state.RequestedAt).TotalMilliseconds;
                    }
                    if (remainingMs <= 0) state.LastWriteStartedAt = Stopwatch.GetTimestamp();
                }
                if (remainingMs > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(remainingMs), _cts.Token).ConfigureAwait(false);
                    continue;
                }

                // Providers own bounded retries; keep all hardware calls off the UI thread.
                bool succeeded = WriteBrightness(monitorId, targetPercent);
                lock (_lifecycleLock)
                {
                    if (succeeded)
                    {
                        state.AppliedPercent = targetPercent;
                        state.HasAppliedPercent = true;
                    }
                    if (!_disposed && state.Version != version) continue;
                    if (!_disposed && state.Smooth && succeeded && state.AppliedPercent != state.LatestPercent) continue;
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

    private static int NextSmoothPercent(MonitorDebounceState state)
    {
        double progress = Math.Clamp(Stopwatch.GetElapsedTime(state.SmoothStartedAt).TotalMilliseconds / SmoothDurationMs, 0, 1);
        double eased = 1 - (1 - progress) * (1 - progress);
        double desired = state.SmoothFromPercent + (state.LatestPercent - state.SmoothFromPercent) * eased;
        int remaining = state.LatestPercent - state.AppliedPercent;
        int direction = Math.Sign(remaining);
        int step = (int)Math.Ceiling(Math.Max(0, (desired - state.AppliedPercent) * direction));
        int maxStep = Math.Abs(state.LatestPercent - state.SmoothFromPercent) <= 5 ? 2 : 5;
        // Always move toward the newest target, even if it changed during a slow
        // native write. Small notches must not become a single abrupt hardware jump.
        step = Math.Clamp(step, 1, Math.Min(maxStep, Math.Abs(remaining)));
        return state.AppliedPercent + direction * step;
    }

    private bool WriteBrightness(string monitorId, int percent)
    {
        if (_cts.IsCancellationRequested) return false;

        // Providers own their bounded hardware retries; do not multiply them here.
        try
        {
            if (_brightnessProvider.TrySetBrightness(monitorId, percent))
            {
                _settingsService.SetBrightness(monitorId, percent);
                return true;
            }
        }
        catch { /* Keep a failed write from discarding a newer queued request. */ }
        return false;
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
