using System.Collections.Concurrent;
using BrightnessTray.Native;
using BrightnessTray.Services;

internal static class PerformanceRegression
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    internal static void SharedScan()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider { Read = _ => { started.Set(); Wait(release); return Monitors("A"); } };
        var discovery = new MonitorDiscoveryService(provider);
        var first = discovery.GetMonitorsAsync();
        try
        {
            Wait(started);
            var second = discovery.GetMonitorsAsync();
            Check(ReferenceEquals(first, second), "Concurrent consumers must share the hardware scan.");
            using var cancellation = new CancellationTokenSource();
            var closedFlyout = second.WaitAsync(cancellation.Token);
            cancellation.Cancel();
            try { Await(closedFlyout); throw new Exception("Expected local cancellation."); }
            catch (OperationCanceledException) { }
            Check(!first.IsCompleted, "Canceling a consumer must not cancel the shared scan.");
        }
        finally { release.Set(); }
        Equal("A", Await(first).Monitors[0].Id);
        Equal(1, provider.ReadCount);
    }

    internal static void InvalidatedScan()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider { Read = call =>
        {
            if (call == 1) { started.Set(); Wait(release); }
            return Monitors(call == 1 ? "OLD" : "NEW");
        } };
        var discovery = new MonitorDiscoveryService(provider);
        var scan = discovery.GetMonitorsAsync();
        try
        {
            Wait(started);
            discovery.Invalidate();
            discovery.Invalidate();
            Check(ReferenceEquals(scan, discovery.GetMonitorsAsync()), "Invalidations must share the existing scan worker.");
        }
        finally { release.Set(); }
        var result = Await(scan);
        Equal("NEW", result.Monitors[0].Id);
        Equal(2, provider.ReadCount);
        Check(discovery.IsCurrent(result), "The returned result must match the latest topology.");
        discovery.Invalidate();
        Check(!discovery.IsCurrent(result), "Already returned results must become stale after a topology event.");
    }

    internal static void FailedScan()
    {
        var provider = new FakeProvider { Read = call => call == 1 ? throw new IOException() : Monitors("A") };
        var discovery = new MonitorDiscoveryService(provider);
        try { Await(discovery.GetMonitorsAsync()); throw new Exception("Expected a failed scan."); }
        catch (IOException) { }
        Equal("A", Await(discovery.GetMonitorsAsync()).Monitors[0].Id);
        Equal(2, provider.ReadCount);
    }

    internal static void FailedInvalidatedScan()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider { Read = call =>
        {
            if (call == 1) { started.Set(); Wait(release); throw new IOException(); }
            return Monitors("NEW");
        } };
        var discovery = new MonitorDiscoveryService(provider);
        var scan = discovery.GetMonitorsAsync();
        try { Wait(started); discovery.Invalidate(); }
        finally { release.Set(); }
        Equal("NEW", Await(scan).Monitors[0].Id);
        Equal(2, provider.ReadCount);
    }

    internal static void ReadVersion()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider { Read = _ => { started.Set(); Wait(release); return Monitors("A"); } };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        var discovery = new MonitorDiscoveryService(provider, brightness);
        var scan = discovery.GetMonitorsAsync();
        try
        {
            Wait(started);
            brightness.RequestBrightness("A", 80);
            Await(brightness.WaitForPendingWritesAsync());
            Check(ReferenceEquals(scan, discovery.GetMonitorsAsync()), "The late consumer must join the same scan.");
        }
        finally { release.Set(); }
        var result = Await(scan);
        Equal(80, brightness.ResolveReadBrightness("A", 20, result.ReadVersion));
        Check(brightness.HasNewerRequest("A", result.ReadVersion), "Startup restore must not overwrite a newer user request.");
        Equal(20, brightness.ResolveReadBrightness("A", 20, brightness.CaptureReadVersion()));
    }

    internal static void SliderBurst()
    {
        var provider = new FakeProvider();
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        long oldVersion = brightness.CaptureReadVersion();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) brightness.RequestBrightness("A", i % 101);
        brightness.RequestBrightness("a", 73);
        for (int i = 0; i < 100; i++) brightness.RequestBrightness("A", 73);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Check(allocated < 8 * 1024, $"A slider burst allocated {allocated} bytes on the request thread.");
        Console.WriteLine($"  Slider burst: 1101 requests, {allocated} request-thread bytes, {brightness.WorkerStartCount} worker.");
        Equal(1, brightness.WorkerStartCount);
        Equal(73, brightness.ResolveReadBrightness("A", 10, oldVersion));
        Await(brightness.WaitForPendingWritesAsync());
        Equal(1, provider.Writes.Count);
        Equal(73, provider.Writes.Single().Percent);
        Equal(73, settings.Service.GetSettings().MonitorBrightness["a"]);
        long idleVersion = brightness.CaptureReadVersion();
        Await(brightness.WaitForPendingWritesAsync());
        Equal(idleVersion, brightness.CaptureReadVersion());
        // An external change may have occurred; completed requests must not suppress a new write.
        brightness.RequestBrightness("A", 73);
        Await(brightness.WaitForPendingWritesAsync());
        Equal(2, provider.Writes.Count);
    }

    internal static void WheelResponse()
    {
        using var firstWrite = new ManualResetEventSlim();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long firstWriteMs = -1;
        var provider = new FakeProvider { Write = (_, _) =>
        {
            Interlocked.CompareExchange(ref firstWriteMs, clock.ElapsedMilliseconds, -1);
            firstWrite.Set();
            return true;
        } };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        var wheel = new TrayWheelSelection();
        int current = 50;
        for (int i = 0; i < 6; i++)
        {
            current = wheel.GetBrightness("A", current, 120);
            brightness.RequestSmoothBrightness("A", 50, current);
            Thread.Sleep(40);
        }
        long scrollingEndedMs = clock.ElapsedMilliseconds;
        Await(brightness.WaitForPendingWritesAsync());
        Console.WriteLine($"  Wheel response: first write {firstWriteMs} ms; scrolling ended {scrollingEndedMs} ms; {provider.Writes.Count} writes.");
        Check(firstWriteMs < scrollingEndedMs, "Brightness must start changing during sustained scrolling, not after it stops.");
        Equal(80, provider.Writes.Last().Percent);
        Equal(1, brightness.WorkerStartCount);
    }

    internal static void WheelTransition()
    {
        var provider = new FakeProvider();
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        var wheel = new TrayWheelSelection();
        brightness.RequestSmoothBrightness("A", 50, wheel.GetBrightness("A", 50, 120));
        Await(brightness.WaitForPendingWritesAsync());
        var values = provider.Writes.Select(w => w.Percent).ToArray();
        Console.WriteLine($"  Wheel transition: 50 -> {string.Join(" -> ", values)}.");
        Check(values.Length > 1 && values[0] < 55, "A wheel notch must pass through intermediate hardware brightness values, not jump straight to its target.");
        Equal(55, values[^1]);
        Equal(55, settings.Service.GetSettings().MonitorBrightness["A"]);
        int previous = 50;
        foreach (int value in values)
        {
            Check(value > previous && value - previous <= 2, "A single notch must rise monotonically in small steps.");
            previous = value;
        }
        // A stale discovery baseline must not cause a jump back on the next notch.
        brightness.RequestSmoothBrightness("A", 50, 60);
        Await(brightness.WaitForPendingWritesAsync());
        Check(provider.Writes.Skip(values.Length).All(w => w.Percent > 55), "A new transition must start from the last applied brightness.");
        Equal(60, provider.Writes.Last().Percent);
        int beforeDown = provider.Writes.Count;
        brightness.RequestSmoothBrightness("A", 50, 55);
        Await(brightness.WaitForPendingWritesAsync());
        previous = 60;
        foreach (var write in provider.Writes.Skip(beforeDown))
        {
            Check(write.Percent < previous && previous - write.Percent <= 2, "Downward transitions must also use small monotonic steps.");
            previous = write.Percent;
        }
        Equal(55, previous);
    }

    internal static void WheelRetarget()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider { Write = (_, _) =>
        {
            started.Set();
            Wait(release);
            Thread.Sleep(60); // Deliberately slower than the wheel pacing interval.
            return true;
        } };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        brightness.RequestSmoothBrightness("A", 50, 55);
        try
        {
            Wait(started);
            brightness.RequestSmoothBrightness("a", 50, 45);
        }
        finally { release.Set(); }
        Await(brightness.WaitForPendingWritesAsync());
        var values = provider.Writes.Select(w => w.Percent).ToArray();
        Check(values.Length > 1, "The reversal must be applied after the in-flight write.");
        for (int i = 1; i < values.Length; i++)
            Check(values[i] < values[i - 1], "A reversed wheel target must not finish the old upward transition first.");
        Equal(45, values[^1]);
        Equal(45, settings.Service.GetSettings().MonitorBrightness["A"]);
        Equal(1, brightness.WorkerStartCount);
        Equal(1, provider.MaxConcurrentWrites);
    }

    internal static void WheelSuperseded()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider { Write = (_, _) => { started.Set(); Wait(release); return true; } };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        brightness.RequestSmoothBrightness("A", 50, 55);
        try
        {
            Wait(started);
            brightness.RequestBrightness("A", 20); // Slider/mute replaces the ramp.
        }
        finally { release.Set(); }
        Await(brightness.WaitForPendingWritesAsync());
        Equal(2, provider.Writes.Count);
        Equal(20, provider.Writes.Last().Percent);
        Equal(20, settings.Service.GetSettings().MonitorBrightness["A"]);
        Equal(1, brightness.WorkerStartCount);
    }

    internal static void WheelPacing()
    {
        var timestamps = new ConcurrentQueue<long>();
        var provider = new FakeProvider { Write = (_, _) =>
        {
            timestamps.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
            return true;
        } };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        brightness.RequestSmoothBrightness("A", 50, 75);
        Await(brightness.WaitForPendingWritesAsync());
        var times = timestamps.ToArray();
        for (int i = 1; i < times.Length; i++)
            Check(System.Diagnostics.Stopwatch.GetElapsedTime(times[i - 1], times[i]).TotalMilliseconds >= 35,
                "Wheel transitions must pace native calls, not flood the monitor with animation frames.");
        Equal(75, provider.Writes.Last().Percent);
        Equal(1, brightness.WorkerStartCount);
        int completedCount = provider.Writes.Count;
        Thread.Sleep(100);
        Equal(completedCount, provider.Writes.Count);
    }

    internal static void WheelFailure()
    {
        var provider = new FakeProvider { Write = (_, _) => false };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        brightness.RequestSmoothBrightness("A", 50, 55);
        Await(brightness.WaitForPendingWritesAsync());
        Equal(1, provider.Writes.Count);
        Equal(0, settings.Service.GetSettings().MonitorBrightness.Count);
        provider.Write = (_, _) => true;
        brightness.RequestSmoothBrightness("A", 50, 55);
        Await(brightness.WaitForPendingWritesAsync());
        Equal(55, settings.Service.GetSettings().MonitorBrightness["A"]);
    }

    internal static void WheelShutdown()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider { Write = (_, _) => { started.Set(); Wait(release); return true; } };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        brightness.RequestSmoothBrightness("A", 50, 55);
        Task stopped;
        try
        {
            Wait(started);
            brightness.RequestSmoothBrightness("A", 50, 80);
            stopped = brightness.StopAsync();
            Check(!stopped.IsCompleted, "Shutdown must wait for the in-flight transition step.");
        }
        finally { release.Set(); }
        Await(stopped);
        Equal(1, provider.Writes.Count);
        int applied = provider.Writes.Single().Percent;
        Check(applied < 55, "Only the in-flight intermediate value should be applied.");
        Equal(applied, settings.Service.GetSettings().MonitorBrightness["A"]);
    }

    internal static void TrayWheelBurst()
    {
        var provider = new FakeProvider();
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        settings.Service.SetTrayWheelMonitorId("b");
        var monitors = Monitors("A").Concat(Monitors("B")).ToArray();
        var target = TrayWheelSelection.ResolveTarget(monitors, settings.Service.GetTrayWheelMonitorId())!;
        var wheel = new TrayWheelSelection();
        brightness.RequestBrightness(target.Id, 40); // Include a pending slider request.
        foreach (int delta in new[] { 60, 60, 120, 120, -120, 240 })
        {
            int current = brightness.ResolveReadBrightness(target.Id, 10, long.MaxValue);
            int next = wheel.GetBrightness(target.Id, current, delta);
            if (next != current) brightness.RequestSmoothBrightness(target.Id, target.CurrentPercent, next);
        }
        Equal(1, brightness.WorkerStartCount);
        Await(brightness.WaitForPendingWritesAsync());
        Check(provider.Writes.All(w => w.Id == "B"), "Only the selected monitor may be written.");
        Equal(60, provider.Writes.Last().Percent);
        Equal(60, settings.Service.GetSettings().MonitorBrightness["b"]);
        Check(!settings.Service.GetSettings().MonitorBrightness.ContainsKey("A"), "Wheel input must not change other displays.");
    }

    internal static void LatestWrite()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider { Write = (_, percent) =>
        {
            if (percent == 10) { started.Set(); Wait(release); throw new IOException("First write failed"); }
            return true;
        } };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        brightness.RequestBrightness("A", 10);
        try
        {
            Wait(started);
            brightness.RequestBrightness("A", 80);
        }
        finally { release.Set(); }
        Await(brightness.WaitForPendingWritesAsync());
        Equal(1, brightness.WorkerStartCount);
        Equal(2, provider.Writes.Count);
        Equal(80, provider.Writes.Last().Percent);
        Equal(80, settings.Service.GetSettings().MonitorBrightness["A"]);
        Equal(1, provider.MaxConcurrentWrites);
    }

    internal static void IndependentMonitors()
    {
        using var started = new ManualResetEventSlim();
        using var second = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider { Write = (id, _) =>
        {
            if (id == "A") { started.Set(); Wait(release); }
            else second.Set();
            return true;
        } };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        brightness.RequestBrightness("A", 10);
        try { Wait(started); brightness.RequestBrightness("B", 60); Wait(second); }
        finally { release.Set(); }
        Await(brightness.WaitForPendingWritesAsync());
        Equal(2, provider.Writes.Count);
        Equal(60, settings.Service.GetSettings().MonitorBrightness["B"]);
    }

    internal static void Shutdown()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var provider = new FakeProvider { Write = (_, _) => { started.Set(); Wait(release); return true; } };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        brightness.RequestBrightness("A", 40);
        Task stopped;
        try
        {
            Wait(started);
            brightness.RequestBrightness("A", 80);
            stopped = brightness.StopAsync();
            Check(!stopped.IsCompleted, "Shutdown must wait for the native call to return.");
        }
        finally { release.Set(); }
        Await(stopped);
        Equal(1, provider.Writes.Count);
        Equal(40, settings.Service.GetSettings().MonitorBrightness["A"]);
        brightness.RequestBrightness("A", 90);
        Equal(1, provider.Writes.Count);
        Check(ReferenceEquals(stopped, brightness.StopAsync()), "Shutdown must be idempotent.");
    }

    internal static void CancelDebounce()
    {
        var provider = new FakeProvider();
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        brightness.RequestBrightness("A", 40);
        Await(brightness.StopAsync());
        Equal(0, provider.Writes.Count);
        Equal(0, settings.Service.GetSettings().MonitorBrightness.Count);
    }

    internal static void FailedWrite()
    {
        var provider = new FakeProvider { Write = (_, _) => false };
        using var settings = new SettingsFixture();
        using var brightness = new BrightnessService(provider, settings.Service);
        brightness.RequestBrightness("A", 40);
        Await(brightness.WaitForPendingWritesAsync());
        Equal(0, settings.Service.GetSettings().MonitorBrightness.Count);
        provider.Write = (_, _) => true;
        brightness.RequestBrightness("A", 40);
        Await(brightness.WaitForPendingWritesAsync());
        Equal(40, settings.Service.GetSettings().MonitorBrightness["A"]);
    }

    internal static void DdcRange()
    {
        int reads = 0;
        bool writeSucceeds = true;
        uint written = 0;
        uint min = 20, max = 100;
        var provider = new DdcCiProvider((IntPtr _, out uint minimum, out uint current, out uint maximum) =>
        {
            reads++; minimum = min; current = min; maximum = max; return true;
        }, (_, value) => { written = value; return writeSucceeds; });
        provider.RememberRange("A", 20, 100, provider.CacheGeneration);
        Check(provider.TrySetPhysicalBrightness("a", IntPtr.Zero, 50), "Cached write failed.");
        Equal(60u, written);
        Equal(0, reads);
        Check(provider.TrySetPhysicalBrightness("A", IntPtr.Zero, 200), "Clamped write failed.");
        Equal(100u, written);
        provider.InvalidateCache();
        min = 10; max = 210;
        Check(provider.TrySetPhysicalBrightness("A", IntPtr.Zero, 50), "Reload after reconnect failed.");
        Equal(110u, written);
        Equal(1, reads);
        writeSucceeds = false;
        Check(!provider.TrySetPhysicalBrightness("A", IntPtr.Zero, 50), "Failed native write was reported as successful.");
        writeSucceeds = true;
        Check(provider.TrySetPhysicalBrightness("A", IntPtr.Zero, 50), "Fault recovery failed.");
        Equal(2, reads);
        provider.InvalidateCache();
        min = 100; max = 10; written = 999;
        Check(!provider.TrySetPhysicalBrightness("A", IntPtr.Zero, 50), "Invalid range must fail.");
        Equal(999u, written);
        min = max = 60;
        Check(provider.TrySetPhysicalBrightness("A", IntPtr.Zero, 50), "Fixed brightness range should be safe.");
        Equal(60u, written);
    }

    internal static void OldDdcRange()
    {
        int reads = 0;
        var provider = new DdcCiProvider((IntPtr _, out uint min, out uint current, out uint max) =>
        { reads++; min = current = 10; max = 210; return true; }, (_, value) => value == 110);
        long oldGeneration = provider.CacheGeneration;
        provider.InvalidateCache();
        provider.RememberRange("A", 0, 100, oldGeneration);
        Check(provider.TrySetPhysicalBrightness("A", IntPtr.Zero, 50), "Stale range replaced new topology.");
        Equal(1, reads);
    }

    internal static void WmiNames()
    {
        int queries = 0;
        bool fail = false;
        var provider = new WmiProvider(() =>
        {
            queries++;
            if (fail) throw new IOException();
            return new Dictionary<string, string> { ["A"] = "Panel " + queries };
        });
        Equal("Panel 1", provider.GetFriendlyNames()["A"]);
        Equal("Panel 1", provider.GetFriendlyNames()["A"]);
        Equal(1, queries);
        provider.InvalidateCache();
        fail = true;
        Equal(0, provider.GetFriendlyNames().Count);
        fail = false;
        Equal("Panel 3", provider.GetFriendlyNames()["A"]);
        Equal(3, queries);
    }

    internal static void OldWmiNames()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int queries = 0;
        var provider = new WmiProvider(() =>
        {
            int call = Interlocked.Increment(ref queries);
            if (call == 1) { started.Set(); Wait(release); }
            return new Dictionary<string, string> { ["A"] = "Panel " + call };
        });
        var load = Task.Run(provider.GetFriendlyNames);
        try { Wait(started); provider.InvalidateCache(); }
        finally { release.Set(); }
        Await(load);
        Equal("Panel 2", provider.GetFriendlyNames()["A"]);
        Equal(2, queries);
    }

    internal static void CompositeInvalidation()
    {
        var first = new FakeProvider();
        var second = new FakeProvider();
        var provider = new CompositeBrightnessProvider(first, second);
        provider.InvalidateCache();
        Equal(1, first.Invalidations);
        Equal(1, second.Invalidations);
    }

    private static IReadOnlyList<MonitorInfo> Monitors(string id) => new[]
    {
        new MonitorInfo { Id = id, Name = id, MinBrightness = 0, CurrentBrightness = 20, MaxBrightness = 100 }
    };

    private static void Wait(ManualResetEventSlim signal)
    {
        if (!signal.Wait(Timeout)) throw new TimeoutException("A test operation did not signal.");
    }

    private static void Await(Task task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static T Await<T>(Task<T> task) => task.WaitAsync(Timeout).GetAwaiter().GetResult();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual),
        $"Expected {expected}, got {actual}.");

    private sealed class FakeProvider : IBrightnessProvider
    {
        private int _readCount;
        private int _activeWrites;
        private int _maxConcurrentWrites;
        internal int ReadCount => _readCount;
        internal int Invalidations;
        internal int MaxConcurrentWrites => _maxConcurrentWrites;
        internal readonly ConcurrentQueue<(string Id, int Percent)> Writes = new();
        internal Func<int, IReadOnlyList<MonitorInfo>> Read = _ => Monitors("A");
        internal Func<string, int, bool> Write = (_, _) => true;

        public IReadOnlyList<MonitorInfo> GetMonitors() => Read(Interlocked.Increment(ref _readCount));
        public void InvalidateCache() => Interlocked.Increment(ref Invalidations);
        public bool TrySetBrightness(string monitorId, int percent)
        {
            int active = Interlocked.Increment(ref _activeWrites);
            int previous;
            do { previous = _maxConcurrentWrites; }
            while (active > previous && Interlocked.CompareExchange(ref _maxConcurrentWrites, active, previous) != previous);
            try { Writes.Enqueue((monitorId, percent)); return Write(monitorId, percent); }
            finally { Interlocked.Decrement(ref _activeWrites); }
        }
    }

    private sealed class SettingsFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "BrightnessTray.RegressionTests." + Guid.NewGuid().ToString("N"));
        internal SettingsService Service { get; }
        internal SettingsFixture()
        {
            Service = new SettingsService(new NoUiContext(), Path.Combine(_directory, "settings.json"));
        }
        public void Dispose()
        {
            Service.Dispose();
            if (Directory.Exists(_directory))
            {
                File.Delete(Path.Combine(_directory, "settings.json"));
                Directory.Delete(_directory);
            }
        }
    }

    private sealed class NoUiContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) { }
    }
}
