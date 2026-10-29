using System.Diagnostics;
using BrightnessTray.UI;

internal static class UiUpdateThrottleRegression
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Verify(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

    private static void Verify()
    {
        int updates = 0;
        int latest = 0;
        var displayed = new List<int>();
        using var throttle = new UiUpdateThrottle(() => { updates++; displayed.Add(latest); });
        for (int i = 0; i < 12; i++)
        {
            latest = (i + 1) * 5;
            throttle.Request();
            Pump(25);
        }
        int duringInput = updates;
        Pump(200);
        Console.WriteLine($"  Tooltip updates: {duringInput} while input was active, {updates} total.");
        if (duringInput == 0)
            throw new InvalidOperationException("Repeated changes must update the tooltip during input, not debounce every number away until scrolling stops.");
        if (!displayed.Any(value => value < latest) || displayed[^1] != latest)
            throw new InvalidOperationException("The tooltip must display intermediate percentages and finish on the latest value.");
        int settled = updates;
        Pump(100);
        if (updates != settled) throw new InvalidOperationException("Tooltip updates must stop when idle.");
        throttle.Request();
        throttle.Cancel();
        Pump(200);
        if (updates != settled) throw new InvalidOperationException("Canceled tooltip updates must not fire after close.");
        throttle.Request();
        throttle.Dispose();
        Pump(100);
        if (updates != settled) throw new InvalidOperationException("Disposed tooltip updates must not fire.");
    }

    private static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(2);
        }
    }
}
