namespace BrightnessTray.Services;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class AppSettings
{
    public Dictionary<string, int> MonitorBrightness { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MonitorAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}

public sealed class SettingsService : IDisposable
{
    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrightnessTray");

    private static readonly string SettingsFilePath =
        Path.Combine(SettingsDirectory, "settings.json");

    private readonly object _lock = new();
    private readonly SynchronizationContext _uiSyncContext;
    private AppSettings _settings;
    private bool _isDirty;
    private System.Windows.Forms.Timer? _saveDebounceTimer;
    private bool _disposed;

    public SettingsService()
        : this(SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext())
    {
    }

    public SettingsService(SynchronizationContext uiSyncContext)
    {
        _uiSyncContext = uiSyncContext;
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
            if (_disposed) return false;
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

            ScheduleSave();
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
            if (_disposed) return;
            if (_settings.MonitorBrightness.TryGetValue(monitorId, out int oldVal) && oldVal == clamped)
            {
                return; // Only write when value actually changed
            }

            _settings.MonitorBrightness[monitorId] = clamped;
            ScheduleSave();
        }
    }

    private void ScheduleSave()
    {
        if (_disposed) return;
        _isDirty = true;
        _uiSyncContext.Post(_ => StartSaveTimer(), null);
    }

    private void StartSaveTimer()
    {
        lock (_lock)
        {
            // A queued callback may arrive after a flush or shutdown.
            if (_disposed || !_isDirty) return;

            if (_saveDebounceTimer == null)
            {
                _saveDebounceTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _saveDebounceTimer.Tick += OnSaveDebounceTick;
            }
            else
            {
                _saveDebounceTimer.Stop();
            }

            _saveDebounceTimer.Start();
        }
    }

    private void OnSaveDebounceTick(object? sender, EventArgs e)
    {
        StopAndDisposeSaveTimer();
        Flush();
    }

    private void StopAndDisposeSaveTimer()
    {
        if (_saveDebounceTimer != null)
        {
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Tick -= OnSaveDebounceTick;
            _saveDebounceTimer.Dispose();
            _saveDebounceTimer = null;
        }
    }

    public void Flush()
    {
        StopAndDisposeSaveTimer();
        lock (_lock)
        {
            if (_isDirty && SaveInternal())
            {
                _isDirty = false;
            }
        }
    }

    private static AppSettings LoadInternal()
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

            return ParseSettings(json);
        }
        catch
        {
            // Corrupt file or I/O error: gracefully return empty default settings, never throw
            return new AppSettings();
        }
    }

    internal static AppSettings ParseSettings(string json)
    {
        var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
        var settings = new AppSettings();

        // JSON dictionaries do not retain our comparer and may explicitly be null.
        if (loaded?.MonitorBrightness != null)
        {
            foreach (var (id, percent) in loaded.MonitorBrightness)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    settings.MonitorBrightness[id] = Math.Clamp(percent, 0, 100);
            }
        }

        if (loaded?.MonitorAliases != null)
        {
            foreach (var (id, rawAlias) in loaded.MonitorAliases)
            {
                string? alias = SanitizeAlias(rawAlias);
                if (!string.IsNullOrWhiteSpace(id) && alias != null)
                    settings.MonitorAliases[id] = alias;
            }
        }

        return settings;
    }

    private bool SaveInternal()
    {
        string? tempFilePath = null;
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(_settings, AppSettingsJsonContext.Default.AppSettings);
            tempFilePath = Path.Combine(SettingsDirectory, $"settings.json.{Guid.NewGuid():N}.tmp");

            File.WriteAllText(tempFilePath, json);
            File.Move(tempFilePath, SettingsFilePath, overwrite: true);
            tempFilePath = null;
            return true;
        }
        catch
        {
            // Silently handle I/O failures, never crash
            return false;
        }
        finally
        {
            if (tempFilePath != null)
            {
                try { File.Delete(tempFilePath); }
                catch { /* Best-effort cleanup after a failed save. */ }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Flush();
    }
}
