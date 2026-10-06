namespace BrightnessTray.Services;

public static class MonitorNameResolver
{
    private static readonly string[] GenericNames =
    {
        "Generic PnP Monitor",
        "Generic Monitor",
        "Default Monitor",
        "Generic Non-PnP Monitor"
    };

    public static string Resolve(string monitorId, string detectedName, int displayIndex, SettingsService settingsService)
    {
        string? alias = settingsService.GetAlias(monitorId);
        if (!string.IsNullOrWhiteSpace(alias))
        {
            return alias;
        }

        return GetDefaultName(detectedName, displayIndex);
    }

    public static string GetDefaultName(string detectedName, int displayIndex)
    {
        if (string.IsNullOrWhiteSpace(detectedName))
        {
            return $"Display {displayIndex}";
        }

        foreach (var generic in GenericNames)
        {
            if (string.Equals(detectedName.Trim(), generic, StringComparison.OrdinalIgnoreCase))
            {
                return $"Display {displayIndex}";
            }
        }

        return detectedName;
    }

    public static bool HasCustomAlias(string monitorId, SettingsService settingsService)
    {
        string? alias = settingsService.GetAlias(monitorId);
        return !string.IsNullOrWhiteSpace(alias);
    }
}
