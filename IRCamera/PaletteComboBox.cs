namespace IRCamera;

internal sealed class PaletteComboBox : ComboBox
{
    public PaletteComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index >= 0 && Enum.TryParse<ThermalPalette>(GetItemText(Items[e.Index]), out var palette))
        {
            var previewWidth = Math.Min(32, e.Bounds.Width / 3);
            var preview = new Rectangle(e.Bounds.Right - previewWidth - 4, e.Bounds.Top + 4,
                previewWidth, Math.Max(1, e.Bounds.Height - 8));
            var textBounds = new Rectangle(e.Bounds.Left + 3, e.Bounds.Top,
                Math.Max(1, preview.Left - e.Bounds.Left - 7), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), e.Font, textBounds,
                Enabled ? e.ForeColor : SystemColors.GrayText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            for (var x = 0; x < preview.Width; x++)
            {
                using var brush = new SolidBrush(ThermalRenderer.ColorAt((byte)(x * 255 / Math.Max(1, preview.Width - 1)), palette));
                e.Graphics.FillRectangle(brush, preview.Left + x, preview.Top, 1, preview.Height);
            }
            e.Graphics.DrawRectangle(SystemPens.ControlDark, preview);
        }
        e.DrawFocusRectangle();
        base.OnDrawItem(e);
    }
}
