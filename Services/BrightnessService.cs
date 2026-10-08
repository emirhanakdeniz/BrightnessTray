namespace BrightnessTray.Services;

using System.Collections.Concurrent;
using BrightnessTray.Native;

public sealed class BrightnessService : IDisposable
{
    private readonly IBrightnessProvider _brightnessProvider;
    private readonly SettingsService _settingsService;
    private readonly ConcurrentDictionary<string, MonitorDebounceState> _monitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lifecycleLock = new();
    private readonly HashSet<Task> _pendingWrites = new();
    private Task? _shutdownTask;
    private long _brightnessVersion;
    private bool _disposed;

    private const int DebounceMs = 120;

    public event Action<string, int>? BrightnessChanged;

    private sealed class MonitorDebounceState : IDisposable
    {
        public readonly object Lock = new();
        public readonly SemaphoreSlim WriteGate = new(1, 1);
        public CancellationTokenSource? DebounceCts;
        public int LatestPercent;
        public long Version;
        public bool IsPending;

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
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            return;
        }

        int clampedPercent = Math.Clamp(percent, 0, 100);
        lock (_lifecycleLock)
        {
            if (_disposed) return;

            var state = _monitors.GetOrAdd(monitorId, _ => new MonitorDebounceState());
            long version = ++_brightnessVersion;
            state.Version = version;
            state.IsPending = true;
            CancellationToken debounceToken;
            lock (state.Lock)
            {
                state.LatestPercent = clampedPercent;
                state.DebounceCts?.Cancel();
                state.DebounceCts?.Dispose();
                state.DebounceCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                debounceToken = state.DebounceCts.Token;
            }

            // Register the operation before shutdown can take its snapshot.
            Task writeTask = Task.Run(() => WriteDebouncedAsync(monitorId, state, debounceToken));
            _pendingWrites.Add(writeTask);
            _ = writeTask.ContinueWith(completed =>
            {
                lock (_lifecycleLock)
                {
                    _pendingWrites.Remove(completed);
                    if (state.Version == version)
                    {
                        state.IsPending = false;
                        state.Version = ++_brightnessVersion;
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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

    private async Task WriteDebouncedAsync(string monitorId, MonitorDebounceState state, CancellationToken debounceToken)
    {
        try
        {
            await Task.Delay(DebounceMs, debounceToken).ConfigureAwait(false);

            // Queued writes must also stop when superseded or shutting down.
            await state.WriteGate.WaitAsync(debounceToken).ConfigureAwait(false);
            try
            {
                debounceToken.ThrowIfCancellationRequested();
                int targetPercent;
                lock (state.Lock)
                {
                    targetPercent = state.LatestPercent;
                }
                WriteBrightness(monitorId, targetPercent);
            }
            finally
            {
                state.WriteGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded or shutting down.
        }
        catch
        {
            // Fail silently, never crash.
        }
    }

    private void WriteBrightness(string monitorId, int percent)
    {
        if (_cts.IsCancellationRequested) return;

        // Providers own their bounded hardware retries; do not multiply them here.
        if (_brightnessProvider.TrySetBrightness(monitorId, percent))
        {
            _settingsService.SetBrightness(monitorId, percent);
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            if (_shutdownTask != null) return _shutdownTask;

            _disposed = true;
            _cts.Cancel();
            _shutdownTask = CompleteShutdownAsync(_pendingWrites.ToArray());
            return _shutdownTask;
        }
    }

    private async Task CompleteShutdownAsync(Task[] pendingWrites)
    {
        // Native writes cannot be canceled mid-call. Keep their resources alive
        // until they return and record any successfully applied brightness.
        await Task.WhenAll(pendingWrites).ConfigureAwait(false);

        foreach (var state in _monitors.Values)
        {
            state.Dispose();
        }
        _monitors.Clear();

        _cts.Dispose();
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }
}
