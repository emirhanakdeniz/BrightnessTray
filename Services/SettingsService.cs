namespace BrightnessTray.Services;

using System.Text.Json;

public sealed class AppSettings
{
    public Dictionary<string, int> MonitorBrightness { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MonitorAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SettingsService
{
    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrightnessTray");

    private static readonly string SettingsFilePath =
        Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _lock = new();
    private AppSettings _settings;

    public event Action<string, string?>? AliasChanged;

    public SettingsService()
    {
        _settings = LoadInternal();
    }

    public AppSettings GetSettings()
    {
        lock (_lock)
        {
            return new AppSettings
            {
                MonitorBrightness = new Dictionary<string, int>(_settings.MonitorBrightness, StringComparer.OrdinalIgnoreCase),
                MonitorAliases = new Dictionary<string, string>(_settings.MonitorAliases, StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    public string? GetAlias(string monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            return null;
        }

        lock (_lock)
        {
            return _settings.MonitorAliases.TryGetValue(monitorId, out string? alias) ? alias : null;
        }
    }

    public bool SetAlias(string monitorId, string? rawAlias)
    {
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            return false;
        }

        string? sanitized = SanitizeAlias(rawAlias);

        lock (_lock)
        {
            bool hasOld = _settings.MonitorAliases.TryGetValue(monitorId, out string? oldAlias);

            if (string.IsNullOrWhiteSpace(sanitized))
            {
                if (!hasOld)
                {
                    return false;
                }
                _settings.MonitorAliases.Remove(monitorId);
            }
            else
            {
                if (hasOld && string.Equals(oldAlias, sanitized, StringComparison.Ordinal))
                {
                    return false;
                }
                _settings.MonitorAliases[monitorId] = sanitized;
            }

            SaveInternal();
        }

        try
        {
            AliasChanged?.Invoke(monitorId, sanitized);
        }
        catch
        {
            // Fail silently
        }

        return true;
    }

    public static string? SanitizeAlias(string? rawAlias)
    {
        if (string.IsNullOrWhiteSpace(rawAlias))
        {
            return null;
        }

        var sb = new System.Text.StringBuilder(rawAlias.Length);
        foreach (char c in rawAlias)
        {
            if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        string trimmed = sb.ToString().Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length > 32 ? trimmed[..32] : trimmed;
    }

    public void SetBrightness(string monitorId, int percent)
    {
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            return;
        }

        int clamped = Math.Clamp(percent, 0, 100);

        lock (_lock)
        {
            if (_settings.MonitorBrightness.TryGetValue(monitorId, out int oldVal) && oldVal == clamped)
            {
                return; // Only write to disk when value actually changed
            }

            _settings.MonitorBrightness[monitorId] = clamped;
            SaveInternal();
        }
    }

    private AppSettings LoadInternal()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(SettingsFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return loaded ?? new AppSettings();
        }
        catch
        {
            // Corrupt file or I/O error: gracefully return empty default settings, never throw
            return new AppSettings();
        }
    }

    private void SaveInternal()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(_settings, JsonOptions);
            string tempFilePath = Path.Combine(SettingsDirectory, $"settings.json.{Guid.NewGuid():N}.tmp");

            File.WriteAllText(tempFilePath, json);
            File.Move(tempFilePath, SettingsFilePath, overwrite: true);
        }
        catch
        {
            // Silently handle I/O failures, never crash
        }
    }
}
