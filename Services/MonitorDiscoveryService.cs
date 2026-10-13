namespace BrightnessTray.Services;

using BrightnessTray.Native;

public sealed class MonitorDiscoveryService
{
    public sealed record Snapshot(long Generation, long ReadVersion, IReadOnlyList<MonitorInfo> Monitors);

    private readonly IBrightnessProvider _provider;
    private readonly BrightnessService? _brightnessService;
    private readonly object _lock = new();
    private Task<Snapshot>? _pendingScan;
    private long _generation;

    public MonitorDiscoveryService(IBrightnessProvider provider, BrightnessService? brightnessService = null)
    {
        _provider = provider;
        _brightnessService = brightnessService;
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _generation++;
            _provider.InvalidateCache();
        }
    }

    public bool IsCurrent(Snapshot snapshot)
    {
        lock (_lock) return snapshot.Generation == _generation;
    }

    public Task<Snapshot> GetMonitorsAsync()
    {
        lock (_lock)
        {
            return _pendingScan ??= Task.Run(Scan);
        }
    }

    private Snapshot Scan()
    {
        // Repeat only if an OS event invalidated a scan already in progress.
        while (true)
        {
            long generation;
            lock (_lock) generation = _generation;
            long readVersion = _brightnessService?.CaptureReadVersion() ?? 0;
            try
            {
                var monitors = _provider.GetMonitors();
                lock (_lock)
                {
                    if (generation != _generation) continue;
                    _pendingScan = null;
                    return new Snapshot(generation, readVersion, monitors);
                }
            }
            catch
            {
                lock (_lock)
                {
                    // Do not lose a topology event arriving during a failed scan.
                    if (generation != _generation) continue;
                    _pendingScan = null;
                    throw;
                }
            }
        }
    }
}
