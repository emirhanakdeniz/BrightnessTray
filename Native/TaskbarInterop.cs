namespace BrightnessTray.Native;

using System.Runtime.InteropServices;

public static class TaskbarInterop
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);
}
