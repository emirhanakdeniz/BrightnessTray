namespace BrightnessTray.Native;

using System.Diagnostics;
using System.Runtime.InteropServices;

public static class MemoryInterop
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    /// <summary>
    /// Instructs the Windows memory manager to trim cold/unused pages from the process working set.
    /// This reduces the resident physical memory footprint while the application is idle.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch
        {
            // Fail silently, never throw
        }
    }
}
