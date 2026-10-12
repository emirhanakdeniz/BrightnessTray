using System.Reflection;
using BrightnessTray.Controls;
using BrightnessTray.UI;

internal static class ChevronPaintingRegression
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyRepaint(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new InvalidOperationException("Chevron repaint failed.", failure);
    }

    private static void VerifyRepaint()
    {
        using var button = new MonitorExpandButton { Size = new Size(24, 22) };
        var onPaint = typeof(MonitorExpandButton).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (bool dark in new[] { true, false })
        {
            button.BackColor = Theme.BackgroundBase(dark);
            button.ForeColor = Theme.FlyoutIcon(dark);
            foreach (bool expanded in new[] { true, false })
            {
                button.IsExpanded = expanded;
                using var bitmap = new Bitmap(button.Width, button.Height);
                using var graphics = Graphics.FromImage(bitmap);
                // ButtonBase sets Opaque, so WM_PAINT skips OnPaintBackground.
                // An old buffer must be cleared by OnPaint itself, including on theme changes.
                graphics.Clear(Color.Magenta);
                using var paint = new PaintEventArgs(graphics, button.ClientRectangle);
                onPaint.Invoke(button, [paint]);
                var glyphBounds = new Rectangle(button.Width / 2 - 5, button.Height / 2 - 4, 10, 8);
                for (int y = 0; y < bitmap.Height; y++)
                    for (int x = 0; x < bitmap.Width; x++)
                        if (!glyphBounds.Contains(x, y) && bitmap.GetPixel(x, y).ToArgb() != button.BackColor.ToArgb())
                            throw new InvalidOperationException($"Stale background at ({x}, {y}); dark={dark}, expanded={expanded}.");
            }
        }
    }
}
