namespace BrightnessTray.Native;

using System.Collections.Concurrent;

public sealed class CompositeBrightnessProvider : IBrightnessProvider
{
    private readonly IReadOnlyList<IBrightnessProvider> _providers;
    private readonly ConcurrentDictionary<string, IBrightnessProvider> _providerMap = new(StringComparer.OrdinalIgnoreCase);

    public CompositeBrightnessProvider(params IBrightnessProvider[] providers)
    {
        _providers = providers;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var allMonitors = new List<MonitorInfo>();

        foreach (var provider in _providers)
        {
            try
            {
                var monitors = provider.GetMonitors();
                foreach (var monitor in monitors)
                {
                    _providerMap[monitor.Id] = provider;
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
        if (_providerMap.TryGetValue(monitorId, out var mappedProvider))
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
                _providerMap[monitorId] = provider;
                return true;
            }
        }

        return false;
    }
}
