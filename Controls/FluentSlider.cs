namespace BrightnessTray.Controls;

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BrightnessTray.Native;
using BrightnessTray.UI;

public sealed class FluentSlider : Control
{
    private readonly DrawingResources _drawingResources = new();
    private int _value;
    private double _visualValue;
    private int _minimum = 0;
    private int _maximum = 100;

    private bool _isHovered;
    private bool _isDragging;
    private bool _isPointerDown;
    private int _dragOffsetX;
    private int _wheelDeltaRemainder;
    private bool _isDarkMode = true;
    private Color _accentColor = Color.FromArgb(0x00, 0x67, 0xC0);

    // Easing animation state
    private System.Windows.Forms.Timer? _easeTimer;
    private readonly Stopwatch _easeStopwatch = new();
    private double _easeStartVal;
    private double _easeTargetVal;
    private double _easeDurationMs = 100.0;

    // Cached GDI pens and brushes to eliminate allocations in hot paths
    private Pen? _unfilledPen;
    private Pen? _filledPen;
    private SolidBrush? _thumbBrush;
    private int _lastScaleDpi = -1;
    private bool _isKeyboardFocused;

    public event EventHandler<int>? ValueChanged;

    [DefaultValue(0)]
    public int Minimum
    {
        get => _minimum;
        set
        {
            if (_minimum != value)
            {
                _minimum = value;
                if (_value < _minimum)
                {
                    Value = _minimum;
                }
                Invalidate();
            }
        }
    }

    [DefaultValue(100)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            if (_maximum != value)
            {
                _maximum = value;
                if (_value > _maximum)
                {
                    Value = _maximum;
                }
                Invalidate();
            }
        }
    }

    [DefaultValue(0)]
    public int Value
    {
        get => _value;
        set => SetValue(value, 100.0);
    }

    private void SetValue(int value, double durationMs)
    {
        int clamped = Math.Clamp(value, _minimum, _maximum);
        if (_value == clamped) return;
        _value = clamped;
        if (_isDragging)
        {
            StopEaseAnimation();
            _visualValue = _value;
        }
        else StartEaseAnimation(_visualValue, _value, durationMs);
        ValueChanged?.Invoke(this, _value);
        Invalidate();
    }

    public FluentSlider()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.Selectable, true);

        BackColor = Color.Transparent;
        TabStop = true;
        Size = new Size(200, 30);
    }

    public void UpdateTheme(bool isDark, Color accentColor)
    {
        _isDarkMode = isDark;
        _accentColor = accentColor;
        DisposeCachedGdiResources();
        Invalidate();
    }

    private void EnsureGdiResources(int trackHeight)
    {
        int curDpi = DeviceDpi;
        if (_unfilledPen == null || _lastScaleDpi != curDpi)
        {
            DisposeCachedGdiResources();
            _lastScaleDpi = curDpi;

            Color unfilledColor = DwmInterop.IsHighContrast() ? SystemColors.WindowText : Theme.TrackUnfilled(_isDarkMode);
            _unfilledPen = new Pen(unfilledColor, trackHeight)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            Color fillColor = Theme.SliderFill(_isDarkMode);
            _filledPen = new Pen(Enabled ? fillColor : Theme.TextDisabled(_isDarkMode), trackHeight)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
        }

        Color dotColor = Enabled ? Theme.SliderFill(_isDarkMode)
            : DwmInterop.IsHighContrast() ? SystemColors.GrayText : Theme.TextDisabled(_isDarkMode);
        if (_thumbBrush == null || _thumbBrush.Color != dotColor)
        {
            _thumbBrush?.Dispose();
            _thumbBrush = new SolidBrush(dotColor);
        }
    }

    private void DisposeCachedGdiResources()
    {
        _drawingResources.Clear();
        _unfilledPen?.Dispose();
        _unfilledPen = null;
        _filledPen?.Dispose();
        _filledPen = null;
        _thumbBrush?.Dispose();
        _thumbBrush = null;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float scale = DeviceDpi / 96f;
        int trackHeight = Math.Max(2, (int)Math.Round(3f * scale));
        int thumbSize = ScalePixels(20);
        int thumbRadius = thumbSize / 2;

        var track = GetTrackBounds();
        int trackLeft = track.Left;
        int trackRight = track.Right;
        int trackWidth = track.Width;
        int centerY = Height / 2;

        if (trackWidth <= 0)
        {
            return;
        }

        int thumbX = GetThumbX(_visualValue);

        EnsureGdiResources(trackHeight);
        _drawingResources.EnsureDpi(DeviceDpi);

        // 1. Draw unfilled track segment across the span
        if (_unfilledPen != null)
        {
            g.DrawLine(_unfilledPen, trackLeft, centerY, trackRight, centerY);
        }

        // 2. Draw filled track segment up to thumb
        if (thumbX > trackLeft && _filledPen != null)
        {
            g.DrawLine(_filledPen, trackLeft, centerY, Math.Min(thumbX, trackRight), centerY);
        }

        // Match the Windows brightness OSD: dark outer disc with a pale center.
        float outerX = thumbX - thumbRadius;
        float outerY = centerY - thumbRadius;
        var outerRect = new RectangleF(outerX, outerY, thumbSize, thumbSize);
        g.FillEllipse(_drawingResources.Brush(Theme.SliderThumb(_isDarkMode)), outerRect);
        if (_thumbBrush != null)
        {
            int centerSize = ScalePixels(_isPointerDown ? 8 : _isHovered && Enabled ? 12 : 10);
            g.FillEllipse(_thumbBrush, thumbX - centerSize / 2f, centerY - centerSize / 2f,
                centerSize, centerSize);
        }

        // 4. Keyboard focus visual (only shown when focused via keyboard)
        if (Focused && _isKeyboardFocused)
        {
            float focusOffset = 2f * scale;
            var focusRect = new RectangleF(
                outerRect.X - focusOffset,
                outerRect.Y - focusOffset,
                outerRect.Width + 2 * focusOffset,
                outerRect.Height + 2 * focusOffset);

            var focusPen = _drawingResources.Pen(DwmInterop.IsHighContrast() ? SystemColors.Highlight : _accentColor, 1f * scale);
            g.DrawEllipse(focusPen, focusRect);
        }
    }

    // Painting and every input path use these fixed endpoints, independent of hover/press state.
    private Rectangle GetTrackBounds()
    {
        int inset = ScalePixels(14);
        return new Rectangle(inset, Height / 2, Math.Max(0, Width - inset * 2), 0);
    }

    private int GetThumbX(double value)
    {
        var track = GetTrackBounds();
        double ratio = _maximum > _minimum ? (value - _minimum) / (_maximum - _minimum) : 0;
        return track.Left + (int)Math.Round(Math.Clamp(ratio, 0, 1) * track.Width);
    }

    private int ValueFromX(int x)
    {
        var track = GetTrackBounds();
        if (track.Width <= 0) return _minimum;
        double fraction = (double)(x - track.Left) / track.Width;
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        return (int)Math.Round(_minimum + fraction * (_maximum - _minimum));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Enabled && e.Button == MouseButtons.Left)
        {
            Focus();
            _isKeyboardFocused = false;
            int thumbX = GetThumbX(_visualValue);
            bool clickedNearThumb = Math.Abs(e.X - thumbX) <= ScalePixels(16);
            _isPointerDown = true;
            Capture = true;

            if (clickedNearThumb)
            {
                // Instant 1:1 drag
                StopEaseAnimation();
                _isDragging = true;
                _dragOffsetX = e.X - thumbX;
                Value = (int)Math.Round(_visualValue);
            }
            else
            {
                // Track click: ease to target over ~100ms, update Value optimistically
                _dragOffsetX = 0;
                Value = ValueFromX(e.X);
            }
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isPointerDown && Enabled)
        {
            _isDragging = true;
            StopEaseAnimation();
            int newVal = ValueFromX(e.X - _dragOffsetX);
            _visualValue = newVal;
            Value = newVal;
            Invalidate();
        }
        else
        {
            bool wasHovered = _isHovered;
            _isHovered = ClientRectangle.Contains(e.Location);
            if (_isHovered != wasHovered)
            {
                Invalidate();
            }
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _isPointerDown)
        {
            _isPointerDown = false;
            _isDragging = false;
            Capture = false;
            Invalidate();
        }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) CancelPointerInteraction();
    }

    private void CancelPointerInteraction()
    {
        _isPointerDown = false;
        _isDragging = false;
        _dragOffsetX = 0;
        if (Capture) Capture = false;
        Invalidate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            CancelPointerInteraction();
            StopEaseAnimation();
            _visualValue = _value;
            _wheelDeltaRemainder = 0;
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled) CancelPointerInteraction();
        DisposeCachedGdiResources();
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _isKeyboardFocused = true;
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _isKeyboardFocused = false;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = false;
        Invalidate();
    }

    public void StepValue(int delta)
    {
        if (!Enabled || delta == 0) return;
        _wheelDeltaRemainder += delta;
        int notches = _wheelDeltaRemainder / SystemInformation.MouseWheelScrollDelta;
        _wheelDeltaRemainder %= SystemInformation.MouseWheelScrollDelta;
        int step = notches * 5;
        int targetVal = Math.Clamp(Value + step, _minimum, _maximum);

        if (targetVal != Value)
        {
            SetValue(targetVal, 80.0);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs h)
        {
            h.Handled = true;
        }
        base.OnMouseWheel(e);
        StepValue(e.Delta);
    }

    private void StartEaseAnimation(double startVal, double targetVal, double durationMs)
    {
        if (!IsHandleCreated || !Visible || !AnimationInterop.IsAnimationEnabled() || startVal == targetVal)
        {
            StopEaseAnimation();
            _visualValue = targetVal;
            Invalidate();
            return;
        }

        _easeStartVal = startVal;
        _easeTargetVal = targetVal;
        _easeDurationMs = durationMs;
        _easeStopwatch.Restart();

        if (_easeTimer == null)
        {
            _easeTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _easeTimer.Tick += OnEaseTimerTick;
        }

        _easeTimer.Start();
    }

    private void OnEaseTimerTick(object? sender, EventArgs e)
    {
        double elapsed = _easeStopwatch.Elapsed.TotalMilliseconds;
        double t = Math.Clamp(elapsed / _easeDurationMs, 0.0, 1.0);

        // Cubic ease-out curve
        double ease = 1.0 - Math.Pow(1.0 - t, 3);
        _visualValue = _easeStartVal + (_easeTargetVal - _easeStartVal) * ease;
        Invalidate();

        if (t >= 1.0)
        {
            _visualValue = _easeTargetVal;
            StopEaseAnimation();
            Invalidate();
        }
    }

    private void StopEaseAnimation()
    {
        if (_easeTimer != null)
        {
            _easeTimer.Stop();
            _easeTimer.Tick -= OnEaseTimerTick;
            _easeTimer.Dispose();
            _easeTimer = null;
        }
    }

    private int ScalePixels(int pixels) => (int)Math.Round(pixels * (DeviceDpi / 96f));

    protected override bool IsInputKey(Keys keyData)
    {
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Left:
            case Keys.Right:
            case Keys.Up:
            case Keys.Down:
            case Keys.Home:
            case Keys.End:
            case Keys.PageUp:
            case Keys.PageDown:
                return true;
            default:
                return base.IsInputKey(keyData);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _isKeyboardFocused = true;
        int delta = 0;
        switch (e.KeyCode)
        {
            case Keys.Left:
            case Keys.Down:
                delta = -1;
                break;
            case Keys.Right:
            case Keys.Up:
                delta = 1;
                break;
            case Keys.PageDown:
                delta = -10;
                break;
            case Keys.PageUp:
                delta = 10;
                break;
            case Keys.Home:
                SetValue(_minimum, 80.0);
                e.Handled = true;
                return;
            case Keys.End:
                SetValue(_maximum, 80.0);
                e.Handled = true;
                return;
        }

        if (delta != 0)
        {
            int targetVal = Math.Clamp(Value + delta, _minimum, _maximum);
            if (targetVal != Value)
            {
                SetValue(targetVal, 80.0);
            }
            e.Handled = true;
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        CancelPointerInteraction();
        StopEaseAnimation();
        _visualValue = _value;
        DisposeCachedGdiResources();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopEaseAnimation();
            DisposeCachedGdiResources();
        }

        base.Dispose(disposing);
    }
}
