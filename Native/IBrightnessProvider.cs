namespace BrightnessTray.Native;

public interface IBrightnessProvider
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    bool TrySetBrightness(string monitorId, int percent);
}
