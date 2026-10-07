namespace BrightnessTray;

using BrightnessTray.Native;

internal static class Program
{
    private const string MutexName = @"Local\BrightnessTray_SingleInstance_Mutex";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--test-lifecycle")
        {
            MemoryInterop.AttachConsole(-1);
            ApplicationConfiguration.Initialize();
            using var ctx = new TrayApplicationContext();
            ctx.RunLifecycleTest();
            return;
        }

        // Prevent multiple instances using a named Mutex
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            return;
        }

        try
        {
            ApplicationConfiguration.Initialize();
            using var context = new TrayApplicationContext();
            Application.Run(context);
        }
        finally
        {
            if (isOnlyInstance)
            {
                try { mutex.ReleaseMutex(); } catch { /* Ignore */ }
            }
        }
    }
}
