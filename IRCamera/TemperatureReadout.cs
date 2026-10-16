namespace IRCamera;

/// <summary>A stable, buffered value with a small palette key instead of a colored text block.</summary>
internal sealed class TemperatureReadout : Label
{
    private Color? markerColor;
    private Font? unitFont;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color? MarkerColor
    {
        get => markerColor;
        set
        {
            if (markerColor == value) return;
            markerColor = value;
            Invalidate();
        }
    }

    public TemperatureReadout()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Padding = new Padding(0, 0, 0, 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Keep the complete value in Text for accessibility and copying, but
        // render its number and unit separately so the measurement leads.
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        unitFont ??= new Font(Font.FontFamily, Math.Max(8F, Font.Size * 0.65F), FontStyle.Regular);
        var hasUnit = Text.EndsWith(" °C", StringComparison.Ordinal);
        var number = hasUnit ? Text[..^3] : Text;
        var numberSize = TextRenderer.MeasureText(e.Graphics, number, Font, Size.Empty, flags);
        var unitSize = hasUnit ? TextRenderer.MeasureText(e.Graphics, "°C", unitFont, Size.Empty, flags) : Size.Empty;
        var gap = hasUnit ? Math.Max(3, DeviceDpi * 4 / 96) : 0;
        var left = (Width - numberSize.Width - gap - unitSize.Width) / 2;
        var top = (Height - Padding.Vertical - numberSize.Height) / 2 + Padding.Top;
        var ink = Enabled ? ForeColor : SystemColors.GrayText;
        TextRenderer.DrawText(e.Graphics, number, Font, new Point(Math.Max(0, left), top), ink, flags);
        if (hasUnit)
        {
            var unitInk = SystemInformation.HighContrast ? ink : Color.FromArgb(87, 101, 116);
            TextRenderer.DrawText(e.Graphics, "°C", unitFont,
                new Point(Math.Max(0, left) + numberSize.Width + gap, top + numberSize.Height - unitSize.Height - 1), unitInk, flags);
        }
        if (markerColor is not Color color) return;
        var key = new Rectangle((Width - 24) / 2, Height - 5, 24, 3);
        using var fill = new SolidBrush(color);
        using var border = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(170, 178, 186));
        e.Graphics.FillRectangle(fill, key);
        e.Graphics.DrawRectangle(border, key);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        unitFont?.Dispose();
        unitFont = null;
        base.OnFontChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) unitFont?.Dispose();
        base.Dispose(disposing);
    }
}
