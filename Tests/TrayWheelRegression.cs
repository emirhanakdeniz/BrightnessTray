using BrightnessTray.Native;
using BrightnessTray.Services;

internal static class TrayWheelRegression
{
    internal static void Selection()
    {
        var a = Monitor("A");
        var b = Monitor("B");
        var c = Monitor("C");
        Equal<MonitorInfo?>(null, TrayWheelSelection.ResolveTarget([], "A"));
        Equal(a, TrayWheelSelection.ResolveTarget([a], null));
        Equal(a, TrayWheelSelection.ResolveTarget([a], "Disconnected"));
        Equal<MonitorInfo?>(null, TrayWheelSelection.ResolveTarget([a, b], null));
        Equal(b, TrayWheelSelection.ResolveTarget([a, b], "b"));
        Equal<MonitorInfo?>(null, TrayWheelSelection.ResolveTarget([a, c], "B"));
        Equal(a, TrayWheelSelection.ResolveTarget([a], "B"));
        Equal(b, TrayWheelSelection.ResolveTarget([a, b], "B"));
        Equal(b, TrayWheelSelection.ResolveTarget([b, a], "B"));
    }

    internal static void Deltas()
    {
        var wheel = new TrayWheelSelection();
        Equal(55, wheel.GetBrightness("A", 50, 120));
        Equal(45, wheel.GetBrightness("A", 50, -120));
        Equal(65, wheel.GetBrightness("A", 50, 360));
        Equal(100, wheel.GetBrightness("A", 98, 120));
        Equal(0, wheel.GetBrightness("A", 2, -120));
        Equal(5, wheel.GetBrightness("A", 0, 120));
        Equal(95, wheel.GetBrightness("A", 100, -120));
        Equal(50, wheel.GetBrightness("A", 50, 30));
        Equal(50, wheel.GetBrightness("a", 50, 60));
        Equal(55, wheel.GetBrightness("A", 50, 30));
        Equal(50, wheel.GetBrightness("A", 50, -60));
        Equal(45, wheel.GetBrightness("A", 50, -60));
        Equal(50, wheel.GetBrightness("A", 50, 60));
        Equal(50, wheel.GetBrightness("A", 50, -60));
        Equal(50, wheel.GetBrightness("A", 50, 60));
        Equal(50, wheel.GetBrightness("B", 50, 60)); // No remainder crosses targets.
        wheel.Reset();
        Equal(50, wheel.GetBrightness("B", 50, 60)); // Nor visits/topology changes.
    }

    internal static void Persistence()
    {
        Equal<string?>(null, SettingsService.ParseSettings("{}").TrayWheelMonitorId);
        Equal<string?>(null, SettingsService.ParseSettings("""{"TrayWheelMonitorId":null}""").TrayWheelMonitorId);
        Equal<string?>(null, SettingsService.ParseSettings("""{"TrayWheelMonitorId":"  "}""").TrayWheelMonitorId);
        Equal("DISPLAY_A", SettingsService.ParseSettings("""{"TrayWheelMonitorId":"DISPLAY_A"}""").TrayWheelMonitorId);

        string directory = Path.Combine(Path.GetTempPath(), "BrightnessTray.WheelTests." + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            using (var settings = new SettingsService(new NoUiContext(), path))
            {
                settings.SetTrayWheelMonitorId("DISPLAY_A");
                Equal("DISPLAY_A", settings.GetSettings().TrayWheelMonitorId);
                Equal("DISPLAY_A", settings.GetTrayWheelMonitorId());
                settings.SetTrayWheelMonitorId("display_a");
                Equal("DISPLAY_A", settings.GetTrayWheelMonitorId());
                settings.SetAlias("DISPLAY_A", "Desk");
                settings.SetBrightness("DISPLAY_A", 70);
            }
            using (var loaded = new SettingsService(new NoUiContext(), path))
            {
                Equal("DISPLAY_A", loaded.GetTrayWheelMonitorId());
                Equal("Desk", loaded.GetAlias("display_a"));
                Equal(70, loaded.GetSettings().MonitorBrightness["display_a"]);
                loaded.SetTrayWheelMonitorId(null);
            }
            using var cleared = new SettingsService(new NoUiContext(), path);
            Equal<string?>(null, cleared.GetTrayWheelMonitorId());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    private static MonitorInfo Monitor(string id) => new()
    {
        Id = id, Name = id, MinBrightness = 0, CurrentBrightness = 50, MaxBrightness = 100
    };

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private sealed class NoUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }
}
