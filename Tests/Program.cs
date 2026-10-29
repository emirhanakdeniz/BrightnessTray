using BrightnessTray.Native;
using BrightnessTray.Services;

// Runs without monitor access or reading/writing the user's settings file.
var cases = new (string Name, Action Run)[]
{
    ("Null and missing settings dictionaries", () =>
    {
        foreach (string json in new[] { "null", "{}", """{"MonitorBrightness":null,"MonitorAliases":null}""" })
        {
            var settings = SettingsService.ParseSettings(json);
            Equal(0, settings.MonitorBrightness.Count);
            Equal(0, settings.MonitorAliases.Count);
        }
    }),
    ("Case-insensitive monitor IDs after JSON load", () =>
    {
        var settings = SettingsService.ParseSettings("""
            {"MonitorBrightness":{"DISPLAY_A":25,"display_a":75},
             "MonitorAliases":{"DISPLAY_A":"Old","display_a":"Desk"}}
            """);
        Equal(1, settings.MonitorBrightness.Count);
        Equal(75, settings.MonitorBrightness["Display_A"]);
        Equal(1, settings.MonitorAliases.Count);
        Equal("Desk", settings.MonitorAliases["Display_A"]);
    }),
    ("Loaded values are sanitized", () =>
    {
        var settings = SettingsService.ParseSettings("""
            {"MonitorBrightness":{"LOW":-5,"HIGH":105," ":50},
             "MonitorAliases":{"NAME":"  Desk\nDisplay  ","NULL":null,"EMPTY":"\t"," ":"Ignored",
                               "LONG":"1234567890123456789012345678901234567890"}}
            """);
        Equal(2, settings.MonitorBrightness.Count);
        Equal(0, settings.MonitorBrightness["low"]);
        Equal(100, settings.MonitorBrightness["high"]);
        Equal(2, settings.MonitorAliases.Count);
        Equal("DeskDisplay", settings.MonitorAliases["name"]);
        Equal(32, settings.MonitorAliases["long"].Length);
    }),
    ("Hardware brightness range normalization", () =>
    {
        foreach (var (min, current, max, expected) in new (uint, uint, uint, int)[]
        {
            (20, 60, 100, 50), (20, 0, 100, 0), (20, 200, 100, 100),
            (50, 50, 50, 0), (100, 50, 20, 0), (0, uint.MaxValue, uint.MaxValue, 100)
        })
        {
            var monitor = new MonitorInfo
            {
                Id = "test", Name = "test", MinBrightness = min,
                CurrentBrightness = current, MaxBrightness = max
            };
            Equal(expected, monitor.CurrentPercent);
        }
    }),
    ("Chevron repaints recycled pixels in both themes", ChevronPaintingRegression.Run),
    ("Tray wheel selects one display automatically or the saved multi-monitor target", TrayWheelRegression.Selection),
    ("Tray wheel handles notches, partial deltas, limits and target changes", TrayWheelRegression.Deltas),
    ("Tray wheel preferences persist independently of brightness and aliases", TrayWheelRegression.Persistence),
    ("Tray wheel starts hardware writes during sustained scrolling", PerformanceRegression.WheelResponse),
    ("Tray wheel transitions through intermediate hardware levels", PerformanceRegression.WheelTransition),
    ("Tray wheel reversals retarget slow in-flight writes", PerformanceRegression.WheelRetarget),
    ("Slider and mute requests supersede active wheel transitions", PerformanceRegression.WheelSuperseded),
    ("Tray wheel paces hardware writes and stops when idle", PerformanceRegression.WheelPacing),
    ("Tray wheel stops on write failure without saving the target", PerformanceRegression.WheelFailure),
    ("Tray wheel shutdown saves only the completed transition step", PerformanceRegression.WheelShutdown),
    ("Shared monitor scans and local cancellation", PerformanceRegression.SharedScan),
    ("Topology changes discard scans already running", PerformanceRegression.InvalidatedScan),
    ("Failed monitor scans can be retried", PerformanceRegression.FailedScan),
    ("Topology events survive hardware scan failures", PerformanceRegression.FailedInvalidatedScan),
    ("Shared scans preserve their brightness read version", PerformanceRegression.ReadVersion),
    ("Slider burst uses one worker and preserves the final value", PerformanceRegression.SliderBurst),
    ("Tray wheel bursts accumulate pending brightness and write only the selected display", PerformanceRegression.TrayWheelBurst),
    ("New requests survive writes already running", PerformanceRegression.LatestWrite),
    ("Monitors write independently", PerformanceRegression.IndependentMonitors),
    ("Shutdown waits for native writes and records success", PerformanceRegression.Shutdown),
    ("Shutdown cancels queued debounce work", PerformanceRegression.CancelDebounce),
    ("Failed writes do not save settings and can be retried", PerformanceRegression.FailedWrite),
    ("DDC writes reuse normalized ranges and invalidate on faults", PerformanceRegression.DdcRange),
    ("Old DDC discoveries cannot repopulate invalidated ranges", PerformanceRegression.OldDdcRange),
    ("WMI names are cached, invalidated and retried on failure", PerformanceRegression.WmiNames),
    ("WMI loads spanning invalidation cannot repopulate metadata", PerformanceRegression.OldWmiNames),
    ("Composite invalidation reaches every provider", PerformanceRegression.CompositeInvalidation)
};

int failures = 0;
foreach (var (name, run) in cases)
{
    try
    {
        run();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL: {name}: {ex.Message}");
    }
}
return failures == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
