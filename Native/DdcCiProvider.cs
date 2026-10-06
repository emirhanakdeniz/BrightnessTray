namespace BrightnessTray.Native;

using System.Runtime.InteropServices;

public class DdcCiProvider : IBrightnessProvider
{
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 50;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();

        try
        {
            var hMonitors = EnumerateHMonitors();

            foreach (var hMonitor in hMonitors)
            {
                string deviceName = GetDeviceName(hMonitor);

                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
                {
                    continue;
                }

                var physicalMonitors = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors))
                {
                    continue;
                }

                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        var pm = physicalMonitors[i];
                        if (GetMonitorBrightnessWithRetry(pm.hPhysicalMonitor, out uint min, out uint current, out uint max))
                        {
                            string id = GetStableMonitorId(deviceName, i);
                            string desc = pm.szPhysicalMonitorDescription;
                            string displayName = string.IsNullOrWhiteSpace(desc) ? deviceName : desc;

                            result.Add(new MonitorInfo
                            {
                                Id = id,
                                Name = displayName,
                                MinBrightness = min,
                                CurrentBrightness = current,
                                MaxBrightness = max
                            });
                        }
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(count, physicalMonitors);
                }
            }
        }
        catch
        {
            // Fail silently with retry, never crash
        }

        return result;
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
            var hMonitors = EnumerateHMonitors();

            foreach (var hMonitor in hMonitors)
            {
                string deviceName = GetDeviceName(hMonitor);

                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
                {
                    continue;
                }

                var physicalMonitors = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors))
                {
                    continue;
                }

                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        string stableId = GetStableMonitorId(deviceName, i);
                        string fallbackId = $"{deviceName}_{i}";

                        if (string.Equals(stableId, monitorId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(fallbackId, monitorId, StringComparison.OrdinalIgnoreCase))
                        {
                            var pm = physicalMonitors[i];
                            if (!GetMonitorBrightnessWithRetry(pm.hPhysicalMonitor, out uint min, out _, out uint max))
                            {
                                return false;
                            }

                            uint targetBrightness = min + (uint)Math.Round((clampedPercent / 100.0) * (max - min));
                            targetBrightness = Math.Clamp(targetBrightness, min, max);

                            return SetMonitorBrightnessWithRetry(pm.hPhysicalMonitor, targetBrightness);
                        }
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(count, physicalMonitors);
                }
            }
        }
        catch
        {
            // Fail silently, never crash
        }

        return false;
    }

    private static string GetStableMonitorId(string adapterDeviceName, int physicalIndex)
    {
        try
        {
            var displayDevice = new DISPLAY_DEVICE();
            displayDevice.cb = Marshal.SizeOf<DISPLAY_DEVICE>();

            if (EnumDisplayDevices(adapterDeviceName, (uint)physicalIndex, ref displayDevice, 0))
            {
                if (!string.IsNullOrWhiteSpace(displayDevice.DeviceID))
                {
                    return displayDevice.DeviceID.Trim();
                }
            }
        }
        catch
        {
            // Fail silently
        }

        return $"{adapterDeviceName}_{physicalIndex}";
    }

    private static List<IntPtr> EnumerateHMonitors()
    {
        var monitors = new List<IntPtr>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, IntPtr _, IntPtr _) =>
        {
            monitors.Add(hMonitor);
            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    private static string GetDeviceName(IntPtr hMonitor)
    {
        var info = new MONITORINFOEX();
        info.cbSize = Marshal.SizeOf<MONITORINFOEX>();

        if (GetMonitorInfo(hMonitor, ref info))
        {
            return info.szDevice.TrimEnd('\0');
        }

        return $"DISPLAY_{hMonitor}";
    }

    private static bool GetMonitorBrightnessWithRetry(
        IntPtr hPhysicalMonitor,
        out uint min,
        out uint current,
        out uint max)
    {
        min = 0;
        current = 0;
        max = 0;

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            if (GetMonitorBrightness(hPhysicalMonitor, out min, out current, out max))
            {
                return true;
            }

            if (attempt < MaxRetries - 1)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        return false;
    }

    private static bool SetMonitorBrightnessWithRetry(
        IntPtr hPhysicalMonitor,
        uint newBrightness)
    {
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            if (SetMonitorBrightness(hPhysicalMonitor, newBrightness))
            {
                return true;
            }

            if (attempt < MaxRetries - 1)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        return false;
    }

    #region Win32 P/Invoke

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr lprcClip,
        MonitorEnumProc lpfnEnum,
        IntPtr dwData);

    private delegate bool MonitorEnumProc(
        IntPtr hMonitor,
        IntPtr hdcMonitor,
        IntPtr lprcMonitor,
        IntPtr dwData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DISPLAY_DEVICE lpDisplayDevice,
        uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(
        IntPtr hMonitor,
        ref MONITORINFOEX lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor,
        uint dwPhysicalMonitorArraySize,
        [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(
        uint dwPhysicalMonitorArraySize,
        [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(
        IntPtr hMonitor,
        out uint pdwMinimumBrightness,
        out uint pdwCurrentBrightness,
        out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(
        IntPtr hMonitor,
        uint dwNewBrightness);

    #endregion
}
