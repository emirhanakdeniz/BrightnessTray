namespace BrightnessTray.Native;

using System.Runtime.InteropServices;

public static class MemoryInterop
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AttachConsole(int dwProcessId);
}
