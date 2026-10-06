namespace BrightnessTray.Controls;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BrightnessTray.Native;
using BrightnessTray.UI;

public sealed class FluentSlider : Control
{
    private int _value;
    private double _visualValue;
    private int _minimum = 0;
    private int _maximum = 100;

    private bool _isHovered;
    private bool _isDragging;
    private bool _isDarkMode = true;
    private bool _isMicaActive;
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
    private Pen? _outerPen;
    private Pen? _focusPen;
    private SolidBrush? _outerBrush;
    private SolidBrush? _innerBrush;
    private int _lastScaleDpi = -1;

    public event EventHandler<int>? ValueChanged;

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

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, _minimum, _maximum);
            if (_value != clamped)
            {
                _value = clamped;
                ValueChanged?.Invoke(this, _value);
                if (_isDragging || !AnimationInterop.IsAnimationEnabled())
                {
                    _visualValue = _value;
                    Invalidate();
                }
            }
        }
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

    public void UpdateTheme(bool isDark, bool isMica, Color accentColor)
    {
        _isDarkMode = isDark;
        _isMicaActive = isMica;
        _accentColor = accentColor;
        DisposeCachedGdiResources();
        Invalidate();
    }

    private void EnsureGdiResources(float scale, int trackHeight, Color outerBodyColor, Color outerRingColor)
    {
        int curDpi = DeviceDpi;
        if (_unfilledPen == null || _lastScaleDpi != curDpi)
        {
            DisposeCachedGdiResources();
            _lastScaleDpi = curDpi;

            Color unfilledColor = Theme.TrackUnfilled(_isDarkMode);
            _unfilledPen = new Pen(unfilledColor, trackHeight)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            _filledPen = new Pen(_accentColor, trackHeight)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            float focusOffset = Math.Max(2f, 3f * scale);
            _focusPen = new Pen(_isDarkMode ? Color.White : Color.Black, 1.5f);
        }

        // Fast update or reuse brushes
        if (_outerBrush == null || _outerBrush.Color != outerBodyColor)
        {
            _outerBrush?.Dispose();
            _outerBrush = new SolidBrush(outerBodyColor);
        }

        if (_outerPen == null || _outerPen.Color != outerRingColor)
        {
            _outerPen?.Dispose();
            _outerPen = new Pen(outerRingColor, 1f);
        }

        if (_innerBrush == null || _innerBrush.Color != _accentColor)
        {
            _innerBrush?.Dispose();
            _innerBrush = new SolidBrush(_accentColor);
        }
    }

    private void DisposeCachedGdiResources()
    {
        _unfilledPen?.Dispose();
        _unfilledPen = null;
        _filledPen?.Dispose();
        _filledPen = null;
        _outerPen?.Dispose();
        _outerPen = null;
        _focusPen?.Dispose();
        _focusPen = null;
        _outerBrush?.Dispose();
        _outerBrush = null;
        _innerBrush?.Dispose();
        _innerBrush = null;
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        base.OnPaintBackground(pevent);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float scale = DeviceDpi / 96f;
        int trackHeight = Math.Max(2, (int)Math.Round(4f * scale));
        int thumbSize = Math.Max(14, (int)Math.Round(20f * scale));
        int thumbRadius = thumbSize / 2;

        int trackLeft = thumbRadius + (int)Math.Round(2f * scale);
        int trackRight = Width - thumbRadius - (int)Math.Round(2f * scale);
        int trackWidth = trackRight - trackLeft;
        int centerY = Height / 2;

        if (trackWidth <= 0)
        {
            return;
        }

        double ratio = (_maximum > _minimum) ? (_visualValue - _minimum) / (_maximum - _minimum) : 0;
        int thumbX = trackLeft + (int)Math.Round(ratio * trackWidth);

        // Determine theme-aware thumb body and ring colors
        Color outerBodyColor;
        Color outerRingColor;

        if (_isDarkMode)
        {
            if (_isDragging)
            {
                outerBodyColor = Color.FromArgb(0x3B, 0x3B, 0x3B);
                outerRingColor = Color.FromArgb(0x65, 0x65, 0x65);
            }
            else if (_isHovered)
            {
                outerBodyColor = Color.FromArgb(0x52, 0x52, 0x52);
                outerRingColor = Color.FromArgb(0x8A, 0x8A, 0x8A);
            }
            else
            {
                outerBodyColor = Color.FromArgb(0x45, 0x45, 0x45);
                outerRingColor = Color.FromArgb(0x75, 0x75, 0x75);
            }
        }
        else
        {
            if (_isDragging)
            {
                outerBodyColor = Color.FromArgb(0xED, 0xED, 0xED);
                outerRingColor = Color.FromArgb(0xA8, 0xA8, 0xA8);
            }
            else if (_isHovered)
            {
                outerBodyColor = Color.FromArgb(0xF8, 0xF8, 0xF8);
                outerRingColor = Color.FromArgb(0xB8, 0xB8, 0xB8);
            }
            else
            {
                outerBodyColor = Color.FromArgb(0xFF, 0xFF, 0xFF);
                outerRingColor = Color.FromArgb(0xCE, 0xCE, 0xCE);
            }
        }

        EnsureGdiResources(scale, trackHeight, outerBodyColor, outerRingColor);

        // 1. Draw unfilled track segment
        if (_unfilledPen != null)
        {
            g.DrawLine(_unfilledPen, trackLeft, centerY, trackRight, centerY);
        }

        // 2. Draw filled track segment
        if (thumbX > trackLeft && _filledPen != null)
        {
            g.DrawLine(_filledPen, trackLeft, centerY, thumbX, centerY);
        }

        // 3. Draw circular thumb (Windows 11 Fluent style)
        float outerX = thumbX - thumbRadius;
        float outerY = centerY - thumbRadius;
        var outerRect = new RectangleF(outerX, outerY, thumbSize, thumbSize);

        if (_outerBrush != null)
        {
            g.FillEllipse(_outerBrush, outerRect);
        }

        if (_outerPen != null)
        {
            g.DrawEllipse(_outerPen, outerRect.X + 0.5f, outerRect.Y + 0.5f, outerRect.Width - 1f, outerRect.Height - 1f);
        }

        // Inner accent dot (grows slightly on hover and press)
        float dotFraction = _isDragging ? 0.68f : (_isHovered ? 0.60f : 0.48f);
        float innerDiameter = thumbSize * dotFraction;
        float innerRadius = innerDiameter / 2f;
        var innerRect = new RectangleF(thumbX - innerRadius, centerY - innerRadius, innerDiameter, innerDiameter);

        if (_innerBrush != null)
        {
            g.FillEllipse(_innerBrush, innerRect);
        }

        // 4. Focus visual (Windows 11 focus ring)
        if (Focused && _focusPen != null)
        {
            float focusOffset = Math.Max(2f, 3f * scale);
            var focusRect = new RectangleF(
                outerRect.X - focusOffset,
                outerRect.Y - focusOffset,
                outerRect.Width + 2 * focusOffset,
                outerRect.Height + 2 * focusOffset);

            g.DrawEllipse(_focusPen, focusRect);
        }
    }

    private int ValueFromX(int x)
    {
        float scale = DeviceDpi / 96f;
        int thumbRadius = (int)Math.Round(10f * scale);
        int trackLeft = thumbRadius + (int)Math.Round(2f * scale);
        int trackRight = Width - thumbRadius - (int)Math.Round(2f * scale);
        int trackWidth = trackRight - trackLeft;

        if (trackWidth <= 0)
        {
            return _minimum;
        }

        double fraction = (double)(x - trackLeft) / trackWidth;
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        return (int)Math.Round(_minimum + fraction * (_maximum - _minimum));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            Focus();
            int targetVal = ValueFromX(e.X);

            // Determine if clicking directly on thumb vs track jump
            float scale = DeviceDpi / 96f;
            int thumbRadius = (int)Math.Round(10f * scale);
            int trackLeft = thumbRadius + (int)Math.Round(2f * scale);
            int trackRight = Width - thumbRadius - (int)Math.Round(2f * scale);
            int trackWidth = trackRight - trackLeft;

            double curRatio = (_maximum > _minimum) ? (_visualValue - _minimum) / (_maximum - _minimum) : 0;
            int curThumbX = trackLeft + (int)Math.Round(curRatio * trackWidth);

            bool clickedNearThumb = Math.Abs(e.X - curThumbX) <= thumbRadius + ScalePixels(4);

            if (clickedNearThumb)
            {
                // Instant 1:1 drag
                StopEaseAnimation();
                _isDragging = true;
                Capture = true;
                _visualValue = targetVal;
                Value = targetVal;
                Invalidate();
            }
            else
            {
                // Track click: ease to target over ~100ms, update Value optimistically
                Value = targetVal;
                StartEaseAnimation(_visualValue, targetVal, 100.0);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging)
        {
            int newVal = ValueFromX(e.X);
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
        if (e.Button == MouseButtons.Left && _isDragging)
        {
            _isDragging = false;
            Capture = false;
            Invalidate();
        }
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
        if (!_isDragging)
        {
            _isHovered = false;
            Invalidate();
        }
    }

    public void StepValue(int delta)
    {
        int notches = delta / 120;
        int step = notches != 0 ? notches * 5 : (delta > 0 ? 5 : -5);
        int targetVal = Math.Clamp(Value + step, _minimum, _maximum);

        if (targetVal != Value)
        {
            Value = targetVal;
            StartEaseAnimation(_visualValue, targetVal, 80.0);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        StepValue(e.Delta);
    }

    private void StartEaseAnimation(double startVal, double targetVal, double durationMs)
    {
        if (!AnimationInterop.IsAnimationEnabled())
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
                Value = _minimum;
                StartEaseAnimation(_visualValue, _minimum, 80.0);
                e.Handled = true;
                return;
            case Keys.End:
                Value = _maximum;
                StartEaseAnimation(_visualValue, _maximum, 80.0);
                e.Handled = true;
                return;
        }

        if (delta != 0)
        {
            int targetVal = Math.Clamp(Value + delta, _minimum, _maximum);
            if (targetVal != Value)
            {
                Value = targetVal;
                StartEaseAnimation(_visualValue, targetVal, 80.0);
            }
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
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
