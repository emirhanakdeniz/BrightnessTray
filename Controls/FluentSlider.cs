namespace BrightnessTray.Controls;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

public sealed class FluentSlider : Control
{
    private int _value;
    private int _minimum = 0;
    private int _maximum = 100;
    private bool _isHovered;
    private bool _isDragging;
    private bool _isDarkMode = true;
    private bool _isMicaActive;
    private Color _accentColor = Color.FromArgb(0x00, 0x67, 0xC0);

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
                Invalidate();
                ValueChanged?.Invoke(this, _value);
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
        Invalidate();
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

        double ratio = (_maximum > _minimum) ? (double)(_value - _minimum) / (_maximum - _minimum) : 0;
        int thumbX = trackLeft + (int)Math.Round(ratio * trackWidth);

        // 1. Draw unfilled track segment (subtle theme-aware gray)
        Color unfilledColor = _isDarkMode
            ? Color.FromArgb(75, 75, 75)
            : Color.FromArgb(205, 205, 205);

        using (var unfilledPen = new Pen(unfilledColor, trackHeight)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        {
            g.DrawLine(unfilledPen, trackLeft, centerY, trackRight, centerY);
        }

        // 2. Draw filled track segment (system accent color)
        if (thumbX > trackLeft)
        {
            using var filledPen = new Pen(_accentColor, trackHeight)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawLine(filledPen, trackLeft, centerY, thumbX, centerY);
        }

        // 3. Draw circular thumb (Windows 11 Fluent style)
        float outerX = thumbX - thumbRadius;
        float outerY = centerY - thumbRadius;
        var outerRect = new RectangleF(outerX, outerY, thumbSize, thumbSize);

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

        // Outer circular body
        using (var outerBrush = new SolidBrush(outerBodyColor))
        {
            g.FillEllipse(outerBrush, outerRect);
        }

        // 1px outer ring
        using (var outerPen = new Pen(outerRingColor, 1f))
        {
            g.DrawEllipse(outerPen, outerRect.X + 0.5f, outerRect.Y + 0.5f, outerRect.Width - 1f, outerRect.Height - 1f);
        }

        // Inner accent dot (grows slightly on hover and press)
        float dotFraction = _isDragging ? 0.68f : (_isHovered ? 0.60f : 0.48f);
        float innerDiameter = thumbSize * dotFraction;
        float innerRadius = innerDiameter / 2f;
        var innerRect = new RectangleF(thumbX - innerRadius, centerY - innerRadius, innerDiameter, innerDiameter);

        using (var innerBrush = new SolidBrush(_accentColor))
        {
            g.FillEllipse(innerBrush, innerRect);
        }

        // 4. Focus visual (Windows 11 focus ring)
        if (Focused)
        {
            float focusOffset = Math.Max(2f, 3f * scale);
            var focusRect = new RectangleF(
                outerRect.X - focusOffset,
                outerRect.Y - focusOffset,
                outerRect.Width + 2 * focusOffset,
                outerRect.Height + 2 * focusOffset);

            using var focusPen = new Pen(_isDarkMode ? Color.White : Color.Black, 1.5f);
            g.DrawEllipse(focusPen, focusRect);
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
            _isDragging = true;
            Capture = true;
            Value = ValueFromX(e.X);
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDragging)
        {
            Value = ValueFromX(e.X);
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
        Value = Math.Clamp(Value + step, _minimum, _maximum);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        StepValue(e.Delta);
    }

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
                e.Handled = true;
                return;
            case Keys.End:
                Value = _maximum;
                e.Handled = true;
                return;
        }

        if (delta != 0)
        {
            Value = Math.Clamp(Value + delta, _minimum, _maximum);
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
}
