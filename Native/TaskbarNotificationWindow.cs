namespace BrightnessTray.Native;

public sealed class TaskbarNotificationWindow : NativeWindow, IDisposable
{
    private readonly uint _taskbarCreatedMsg;
    private readonly Action _onTaskbarCreated;
    private bool _disposed;

    public TaskbarNotificationWindow(Action onTaskbarCreated)
    {
        _onTaskbarCreated = onTaskbarCreated;
        _taskbarCreatedMsg = TaskbarInterop.RegisterWindowMessage("TaskbarCreated");

        // Top-level invisible popup window to receive broadcast messages (such as TaskbarCreated)
        var cp = new CreateParams
        {
            Caption = "BrightnessTray_TaskbarListener",
            Style = unchecked((int)0x80000000) // WS_POPUP (top-level, no taskbar button, not visible)
        };

        CreateHandle(cp);
    }

    protected override void WndProc(ref Message m)
    {
        if (_taskbarCreatedMsg != 0 && m.Msg == _taskbarCreatedMsg)
        {
            _onTaskbarCreated();
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            DestroyHandle();
        }
    }
}
