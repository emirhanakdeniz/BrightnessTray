namespace BrightnessTray;

using BrightnessTray.Native;

internal static class Program
{
    private const string MutexName = @"Local\BrightnessTray_SingleInstance_Mutex";

    [STAThread]
    private static void Main(string[] args)
    {
        // Support one-off dev command to generate multi-size app.ico
        if (args.Length > 0 && args[0] == "--generate-icon")
        {
            IconRenderer.GenerateAppIconFile("app.ico");
            return;
        }

        // Auto-generate app.ico if not yet created
        if (!File.Exists("app.ico"))
        {
            try
            {
                IconRenderer.GenerateAppIconFile("app.ico");
            }
            catch
            {
                // Silently continue
            }
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
            mutex.ReleaseMutex();
        }
    }
}
