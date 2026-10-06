namespace BrightnessTray.Native;

using System.Runtime.InteropServices;

public static class DisplayConfigInterop
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;

    // DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY constants (wingdi.h / d3dkmddi.h)
    private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS = 6;
    private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;
    private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public uint refreshRateRationalNumerator;
        public uint refreshRateRationalDenominator;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)]
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] modeInfo;
    }

    /// <summary>
    /// Detects whether the current system has any active internal display panels (e.g., laptop screen, eDP, LVDS).
    /// Uses native Win32 CCD API (user32.dll) and battery status without touching WMI or loading System.Management.
    /// </summary>
    public static bool HasInternalDisplay()
    {
        try
        {
            int error = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes);
            if (error == 0 && numPaths > 0)
            {
                var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                var modes = new DISPLAYCONFIG_MODE_INFO[numModes];

                error = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
                if (error == 0)
                {
                    for (int i = 0; i < numPaths; i++)
                    {
                        uint tech = paths[i].targetInfo.outputTechnology;
                        if (tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL ||
                            tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED ||
                            tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED ||
                            tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_LVDS)
                        {
                            return true;
                        }
                    }
                }
            }

            // Fallback: If device has an onboard battery, it is a laptop/mobile machine capable of internal panels
            if (GetSystemPowerStatus(out var powerStatus))
            {
                // BatteryFlag 128 = No system battery (desktop PC); 255 = Unknown status
                if (powerStatus.BatteryFlag != 128 && powerStatus.BatteryFlag != 255)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Fail silently, assume false on any platform error
        }

        return false;
    }
}
