namespace BrightnessTray.Native;

using System.Management;

public class WmiProvider : IBrightnessProvider
{
    private readonly ManagementScope _wmiScope = new(@"root\WMI");

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        // If the laptop lid is closed (or internal display path is inactive in Windows topology), do not show it
        if (!DisplayConfigInterop.IsInternalDisplayActive())
        {
            return monitors;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(_wmiScope, new SelectQuery("WmiMonitorBrightness"));
            using var results = searcher.Get();

            var names = GetFriendlyNames();

            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    bool active = (bool)(mo["Active"] ?? false);
                    if (!active)
                    {
                        continue;
                    }

                    string instanceName = (string)(mo["InstanceName"] ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(instanceName))
                    {
                        continue;
                    }

                    uint current = Convert.ToUInt32(mo["CurrentBrightness"] ?? 0);

                    string name = names.TryGetValue(instanceName, out var friendly) && !string.IsNullOrWhiteSpace(friendly)
                        ? friendly
                        : "Built-in Display";

                    monitors.Add(new MonitorInfo
                    {
                        Id = instanceName,
                        Name = name,
                        MinBrightness = 0,
                        CurrentBrightness = current,
                        MaxBrightness = 100
                    });
                }
            }
        }
        catch (ManagementException)
        {
            // Gracefully handle systems without WMI-controlled internal panels
        }
        catch
        {
            // Fail silently, never crash
        }

        return monitors;
    }

    public bool TrySetBrightness(string monitorId, int percent)
    {
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            return false;
        }

        int clampedPercent = Math.Clamp(percent, 0, 100);

        try
        {
            using var searcher = new ManagementObjectSearcher(_wmiScope, new SelectQuery("WmiMonitorBrightnessMethods"));
            using var results = searcher.Get();

            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    string instanceName = (string)(mo["InstanceName"] ?? string.Empty);
                    if (string.Equals(instanceName, monitorId, StringComparison.OrdinalIgnoreCase))
                    {
                        using var inParams = mo.GetMethodParameters("WmiSetBrightness");
                        inParams["Timeout"] = 1u; // 1 second timeout
                        inParams["Brightness"] = (byte)clampedPercent;

                        mo.InvokeMethod("WmiSetBrightness", inParams, null);
                        return true;
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // Gracefully handle WMI errors
        }
        catch
        {
            // Fail silently
        }

        return false;
    }

    private Dictionary<string, string> GetFriendlyNames()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(_wmiScope, new SelectQuery("WmiMonitorID"));
            using var results = searcher.Get();

            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    string instanceName = (string)(mo["InstanceName"] ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(instanceName))
                    {
                        continue;
                    }

                    string name = DecodeWmiString(mo["UserFriendlyName"] as ushort[]);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        string mfg = DecodeWmiString(mo["ManufacturerName"] as ushort[]);
                        name = !string.IsNullOrWhiteSpace(mfg)
                            ? $"{mfg} Internal Display"
                            : "Built-in Display";
                    }

                    dict[instanceName] = name;
                }
            }
        }
        catch (ManagementException)
        {
            // Gracefully handle
        }
        catch
        {
            // Fail silently
        }

        return dict;
    }

    private static string DecodeWmiString(ushort[]? chars)
    {
        if (chars is null || chars.Length == 0)
        {
            return string.Empty;
        }

        var clean = chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray();
        return new string(clean).Trim();
    }
}
