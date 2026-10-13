namespace BrightnessTray.Native;

public sealed class CompositeBrightnessProvider : IBrightnessProvider
{
    private readonly IReadOnlyList<IBrightnessProvider> _providers;
    private readonly TopologyCache<IBrightnessProvider> _providerMap = new();

    public CompositeBrightnessProvider(params IBrightnessProvider[] providers)
    {
        _providers = providers;
    }

    public void InvalidateCache()
    {
        _providerMap.Invalidate();
        foreach (var provider in _providers) provider.InvalidateCache();
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var allMonitors = new List<MonitorInfo>();
        long generation = _providerMap.Generation;

        foreach (var provider in _providers)
        {
            try
            {
                var monitors = provider.GetMonitors();
                foreach (var monitor in monitors)
                {
                    _providerMap.Store(monitor.Id, provider, generation);
                    allMonitors.Add(monitor);
                }
            }
            catch
            {
                // Fail silently, never crash
            }
        }

        return allMonitors;
    }

    public bool TrySetBrightness(string monitorId, int percent)
    {
        long generation = _providerMap.Generation;
        if (_providerMap.TryGet(monitorId, out var mappedProvider))
        {
            if (mappedProvider.TrySetBrightness(monitorId, percent))
            {
                return true;
            }
        }

        // Fallback: search all providers
        foreach (var provider in _providers)
        {
            if (provider != mappedProvider && provider.TrySetBrightness(monitorId, percent))
            {
                _providerMap.Store(monitorId, provider, generation);
                return true;
            }
        }

        return false;
    }
}
