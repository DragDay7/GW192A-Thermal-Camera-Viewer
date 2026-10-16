using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace IRCamera;

internal sealed class ThermalCanvas : Control
{
    private Bitmap? image;
    private readonly List<(Point Minimum, Point Maximum)> markerHistory = [];
    private TemperatureFrame? markerFrame;
    private bool fadeMarkers;
    private int markerRotation;
    internal static readonly Point DefaultMeasurementPoint = new(48, 48);
    internal Point MeasurementPoint { get; private set; } = DefaultMeasurementPoint;
    private string emptyTitle = "Camera not connected";
    private string emptyDetail = "Plug in the GW192A, then select Connect.";
    internal Color ColdMarkerColor => ThermalRenderer.ColorAt(255, Palette);
    internal Color HotMarkerColor => ThermalRenderer.ColorAt(0, Palette);
    internal static Color MarkerOutline(Color color)
        => 299 * color.R + 587 * color.G + 114 * color.B < 128000 ? Color.White : Color.Black;
    internal int ImageSide => ImageRectangle().Width;
    internal Rectangle ImageBounds => ImageRectangle();
    [DefaultValue(ThermalPalette.Iron)]
    public ThermalPalette Palette { get; set; }
    public event Action<Point?>? ImageHovered;
    public event Action<Point>? MeasurementPointChanged;

    public ThermalCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(18, 20, 24);
        ForeColor = Color.Silver;
        Dock = DockStyle.Fill;
        Cursor = Cursors.Cross;
        AccessibleName = "Thermal image";
        AccessibleDescription = "Left-click to move the measurement cross. Double-click to reset it, or use the right-click menu. Ctrl+Space freezes or resumes; Ctrl+R rotates the image.";
        TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardDoubleClick, true);
    }

    public void SetEmptyMessage(string title, string detail)
    {
        emptyTitle = title;
        emptyDetail = detail;
        if (image is null) Invalidate();
    }

    public void SetImage(Bitmap? next)
    {
        var previous = image;
        image = next;
        if (next is null) SetTemperatureMarkers(null, markerRotation, fadeMarkers);
        previous?.Dispose();
        Invalidate();
    }

    public void SetTemperatureMarkers(TemperatureFrame? temperatures, int rotation, bool fade = false)
    {
        if (ReferenceEquals(temperatures, markerFrame) && rotation == markerRotation && fade == fadeMarkers) return;
        var modeChanged = fade != fadeMarkers;
        if (temperatures is null || modeChanged) markerHistory.Clear();
        if (temperatures is not null && (modeChanged || !ReferenceEquals(temperatures, markerFrame)))
        {
            markerHistory.Insert(0, (temperatures.MinimumPosition, temperatures.MaximumPosition));
            var capacity = fade ? 5 : 1;
            if (markerHistory.Count > capacity) markerHistory.RemoveRange(capacity, markerHistory.Count - capacity);
        }
        markerFrame = temperatures;
        fadeMarkers = fade;
        markerRotation = rotation;
        Invalidate();
    }

    internal static Point MarkerLocation(Point native, Rectangle target, int rotation)
    {
        var (x, y) = (native.X, native.Y);
        (x, y) = rotation switch
        {
            1 => (95 - y, x),
            2 => (95 - x, 95 - y),
            3 => (y, 95 - x),
            _ => (x, y)
        };
        return new Point(target.Left + (2 * x + 1) * target.Width / 192,
            target.Top + (2 * y + 1) * target.Height / 192);
    }

    internal void SetMeasurementPoint(Point point)
    {
        if (point.X is < 0 or >= 96 || point.Y is < 0 or >= 96)
            throw new ArgumentOutOfRangeException(nameof(point));
        if (point == MeasurementPoint) return;
        MeasurementPoint = point;
        Invalidate();
        MeasurementPointChanged?.Invoke(point);
    }

    internal static Point NativePoint(Point displayed, int rotation) => rotation switch
    {
        1 => new Point(displayed.Y, 95 - displayed.X),
        2 => new Point(95 - displayed.X, 95 - displayed.Y),
        3 => new Point(95 - displayed.Y, displayed.X),
        _ => displayed
    };

    private Rectangle ImageRectangle()
    {
        var side = Math.Max(1, Math.Min(ClientSize.Width - 60, ClientSize.Height - 30));
        return new Rectangle((ClientSize.Width - side - 22) / 2, (ClientSize.Height - side) / 2, side, side);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (image is null)
        {
            using var headingFont = new Font(Font, FontStyle.Bold);
            var top = ClientSize.Height / 2 - Font.Height - 10;
            TextRenderer.DrawText(e.Graphics, emptyTitle, headingFont,
                new Rectangle(12, top, Math.Max(1, ClientSize.Width - 24), Font.Height + 6),
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, emptyDetail, Font,
                new Rectangle(16, top + Font.Height + 12, Math.Max(1, ClientSize.Width - 32), Font.Height * 3),
                ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            DrawKeyboardFocus(e.Graphics);
            return;
        }
        var target = ImageRectangle();
        // The renderer already samples grayscale at the exact display size.
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        e.Graphics.DrawImage(image, target, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);

        var scale = new Rectangle(target.Right + 14, target.Top, 12, target.Height);
        using (var gradient = new LinearGradientBrush(scale, Color.White, Color.Black, LinearGradientMode.Vertical))
        {
            gradient.InterpolationColors = new ColorBlend
            {
                Colors = Enumerable.Range(0, 256).Select(i => ThermalRenderer.ColorAt((byte)(255 - i), Palette)).ToArray(),
                Positions = Enumerable.Range(0, 256).Select(i => i / 255F).ToArray()
            };
            e.Graphics.FillRectangle(gradient, scale);
        }
        DrawCross(e.Graphics, MarkerLocation(MeasurementPoint, target, markerRotation), Color.White, 6);
        // Draw oldest first. Each new measured frame reduces prior opacity by
        // 20 percentage points; repainting or rotating does not advance history.
        for (var age = markerHistory.Count - 1; age >= 0; age--)
        {
            var positions = markerHistory[age];
            var alpha = 255 - age * 51;
            DrawCross(e.Graphics, MarkerLocation(positions.Maximum, target, markerRotation),
                Color.FromArgb(alpha, HotMarkerColor), 3, outlined: false);
            DrawCross(e.Graphics, MarkerLocation(positions.Minimum, target, markerRotation),
                Color.FromArgb(alpha, ColdMarkerColor), 3, outlined: false);
        }
        DrawKeyboardFocus(e.Graphics);
    }

    private void DrawKeyboardFocus(Graphics graphics)
    {
        if (Focused && ShowFocusCues && ClientSize.Width > 6 && ClientSize.Height > 6)
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -3, -3), ForeColor, BackColor);
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

    protected override void OnChangeUICues(UICuesEventArgs e)
    {
        base.OnChangeUICues(e);
        if (e.ChangeFocus) Invalidate();
    }

    private static void DrawCross(Graphics graphics, Point position, Color color, int radius, bool outlined = true)
    {
        var (cx, cy) = (position.X, position.Y);
        // Pixel-aligned, odd-sized rectangles keep every arm identical and
        // leave a one-pixel outline around the entire cross, including its tips.
        if (outlined)
        {
            using var outline = new SolidBrush(MarkerOutline(color));
            graphics.FillRectangle(outline, cx - radius - 1, cy - 1, 2 * radius + 3, 3);
            graphics.FillRectangle(outline, cx - 1, cy - radius - 1, 3, 2 * radius + 3);
        }
        using var foreground = new SolidBrush(color);
        graphics.FillRectangle(foreground, cx - radius, cy, 2 * radius + 1, 1);
        // Do not blend the intersection twice when drawing translucent trails.
        graphics.FillRectangle(foreground, cx, cy - radius, 1, radius);
        graphics.FillRectangle(foreground, cx, cy + 1, 1, radius);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var target = ImageRectangle();
        ImageHovered?.Invoke(image is not null && target.Contains(e.Location)
            ? new Point((e.X - target.X) * 96 / target.Width, (e.Y - target.Y) * 96 / target.Height)
            : null);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
        var target = ImageRectangle();
        if (e.Button == MouseButtons.Left && image is not null && target.Contains(e.Location))
        {
            if (e.Clicks == 2)
            {
                SetMeasurementPoint(DefaultMeasurementPoint);
                return;
            }
            var displayed = new Point((e.X - target.Left) * 96 / target.Width, (e.Y - target.Top) * 96 / target.Height);
            SetMeasurementPoint(NativePoint(displayed, markerRotation));
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ImageHovered?.Invoke(null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) image?.Dispose();
        base.Dispose(disposing);
    }
}
