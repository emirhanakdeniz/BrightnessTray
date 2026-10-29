namespace BrightnessTray.UI;

// Coalesces repeated requests into an on-demand UI update; owns no idle timer.
internal sealed class UiUpdateThrottle : IDisposable
{
    private readonly Action _update;
    private System.Windows.Forms.Timer? _timer;
    private bool _disposed;

    internal UiUpdateThrottle(Action update) => _update = update;

    internal void Request()
    {
        if (_disposed || _timer != null) return;
        _timer = new System.Windows.Forms.Timer { Interval = 40 };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Cancel();
        if (!_disposed) _update();
    }

    internal void Cancel()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }
}
