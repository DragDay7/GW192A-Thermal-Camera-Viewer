using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Xbrz;

namespace IRCamera;

internal enum ThermalPalette { Iron, Grayscale, Rainbow }

internal static class ThermalRenderer
{
    private static readonly Color[] Iron = BuildPalette([Color.Black, Color.FromArgb(48, 0, 90),
        Color.FromArgb(146, 20, 88), Color.FromArgb(226, 67, 35), Color.FromArgb(255, 177, 30), Color.FromArgb(255, 255, 210)]);
    private static readonly Color[] Rainbow = BuildPalette([Color.FromArgb(0, 0, 80), Color.Blue,
        Color.Cyan, Color.Lime, Color.Yellow, Color.Red, Color.FromArgb(255, 230, 230)]);

    public static Bitmap Render(ThermalFrame frame, ThermalPalette palette, int rotation, int xbrzFactor = 1,
        ImageFilter filter = ImageFilter.Nearest, int outputSize = 0, int sharpening = 0)
    {
        if (xbrzFactor is < 1 or > 6) throw new ArgumentOutOfRangeException(nameof(xbrzFactor));
        if (rotation is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(rotation));
        if (!Enum.IsDefined(palette)) throw new ArgumentOutOfRangeException(nameof(palette));
        if (outputSize < 0) throw new ArgumentOutOfRangeException(nameof(outputSize));
        var luminance = ImageProcessing.Sharpen(frame.Luminance, ThermalDecoder.Width, sharpening);
        var size = ThermalDecoder.Width * xbrzFactor;
        // Process grayscale before palette lookup, including xBRZ. This avoids
        // creating false palette hues by interpolating already-colored pixels.
        if (xbrzFactor > 1)
        {
            var pixels = luminance.Select(value => Color.FromArgb(value, value, value).ToArgb()).ToArray();
            var scaled = new int[size * size];
            new XbrzScaler(xbrzFactor, withAlpha: false)
                .ScaleImage(pixels, scaled, ThermalDecoder.Width, ThermalDecoder.Height);
            luminance = Array.ConvertAll(scaled, value => (byte)(value & 255));
        }
        var targetSize = outputSize == 0 ? size : outputSize;
        luminance = ImageProcessing.Resize(luminance, size, targetSize, filter);
        var colors = new int[luminance.Length];
        for (var i = 0; i < luminance.Length; i++) colors[i] = ColorAt(luminance[i], palette).ToArgb();
        var bitmap = new Bitmap(targetSize, targetSize, PixelFormat.Format32bppRgb);
        try
        {
            var locked = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { Marshal.Copy(colors, 0, locked.Scan0, colors.Length); }
            finally { bitmap.UnlockBits(locked); }
            bitmap.RotateFlip((RotateFlipType)(rotation % 4));
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public static Color ColorAt(byte value, ThermalPalette palette)
    {
        if (palette == ThermalPalette.Grayscale) return Color.FromArgb(value, value, value);
        return (palette == ThermalPalette.Iron ? Iron : Rainbow)[value];
    }

    private static Color[] BuildPalette(Color[] stops)
    {
        var table = new Color[256];
        for (var value = 0; value < table.Length; value++)
        {
            var position = value / 255.0 * (stops.Length - 1);
            var index = Math.Min((int)position, stops.Length - 2);
            var fraction = position - index;
            var a = stops[index];
            var b = stops[index + 1];
            table[value] = Color.FromArgb((int)(a.R + (b.R - a.R) * fraction),
                (int)(a.G + (b.G - a.G) * fraction), (int)(a.B + (b.B - a.B) * fraction));
        }
        return table;
    }
}
