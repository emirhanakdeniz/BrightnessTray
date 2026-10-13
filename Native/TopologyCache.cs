namespace BrightnessTray.Native;

// Cache only managed metadata, never native handles. Loads run outside the lock
// so OS notifications can invalidate the cache without waiting for hardware.
internal sealed class TopologyCache<T>
{
    private readonly object _lock = new();
    private readonly Dictionary<string, T> _values = new(StringComparer.OrdinalIgnoreCase);
    private long _generation;

    internal long Generation
    {
        get { lock (_lock) return _generation; }
    }

    internal bool TryGet(string id, out T value)
    {
        lock (_lock) return _values.TryGetValue(id, out value!);
    }

    internal void Store(string id, T value, long generation)
    {
        lock (_lock)
        {
            if (generation == _generation) _values[id] = value;
        }
    }

    internal T GetOrLoad(string id, Func<T> load)
    {
        long generation;
        lock (_lock)
        {
            if (_values.TryGetValue(id, out var value)) return value;
            generation = _generation;
        }

        T loaded = load();
        Store(id, loaded, generation);
        return loaded;
    }

    internal void Invalidate()
    {
        lock (_lock)
        {
            _generation++;
            _values.Clear();
        }
    }
}
