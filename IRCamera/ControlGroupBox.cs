using System.Drawing.Drawing2D;

namespace IRCamera;

/// <summary>Buffered group chrome; retains native grouping, layout, and keyboard navigation.</summary>
internal sealed class ControlGroupBox : GroupBox
{
    private Font? headingFont;

    public ControlGroupBox()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast || Width < 20 || Height < Font.Height + 8)
        {
            base.OnPaint(e);
            return;
        }

        headingFont ??= new Font(Font, FontStyle.Bold);
        var scale = DeviceDpi / 96F;
        var border = new RectangleF(0.5F, Font.Height / 2F, Width - 1F, Height - Font.Height / 2F - 1F);
        var diameter = Math.Min(8 * scale, Math.Min(border.Width, border.Height));
        using var path = new GraphicsPath();
        path.AddArc(border.Left, border.Top, diameter, diameter, 180, 90);
        path.AddArc(border.Right - diameter, border.Top, diameter, diameter, 270, 90);
        path.AddArc(border.Right - diameter, border.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(border.Left, border.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(Color.White);
        using var outline = new Pen(Color.FromArgb(207, 215, 224));
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(outline, path);
        e.Graphics.Restore(state);

        var size = TextRenderer.MeasureText(e.Graphics, Text, headingFont, Size.Empty, TextFormatFlags.NoPadding);
        var heading = new Rectangle((int)(10 * scale), 0, size.Width + (int)(8 * scale), Font.Height);
        using var background = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(background, heading);
        TextRenderer.DrawText(e.Graphics, Text, headingFont, heading, Color.FromArgb(63, 79, 98),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        headingFont?.Dispose();
        headingFont = null;
        base.OnFontChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) headingFont?.Dispose();
        base.Dispose(disposing);
    }
}
