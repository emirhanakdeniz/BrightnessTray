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
    })
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
