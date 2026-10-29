namespace BrightnessTray.Services;

using BrightnessTray.Native;

// Keeps wheel policy independent of Windows input and hardware access.
internal sealed class TrayWheelSelection
{
    private string? _targetId;
    private int _remainder;

    internal static MonitorInfo? ResolveTarget(IReadOnlyList<MonitorInfo> monitors, string? selectedId)
    {
        if (monitors.Count == 1) return monitors[0];
        if (string.IsNullOrWhiteSpace(selectedId)) return null;
        return monitors.FirstOrDefault(m => string.Equals(m.Id, selectedId, StringComparison.OrdinalIgnoreCase));
    }

    internal int GetBrightness(string targetId, int currentPercent, int wheelDelta)
    {
        if (!string.Equals(_targetId, targetId, StringComparison.OrdinalIgnoreCase)) Reset();
        _targetId = targetId;
        _remainder += wheelDelta;
        int notches = _remainder / 120;
        _remainder %= 120;
        return Math.Clamp(currentPercent + notches * 5, 0, 100);
    }

    internal void Reset()
    {
        _targetId = null;
        _remainder = 0;
    }
}
