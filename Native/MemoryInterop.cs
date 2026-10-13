namespace BrightnessTray.Native;

using System.Runtime.InteropServices;

public static class MemoryInterop
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("user32.dll")]
    internal static extern uint GetGuiResources(IntPtr process, uint flags);
}
