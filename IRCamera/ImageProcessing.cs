namespace IRCamera;

internal enum ImageFilter { Nearest, Bicubic }

/// <summary>Display-only processing of grayscale data, never radiometric sensor values.</summary>
internal static class ImageProcessing
{
    private sealed record Tap(int Index, float Weight);
    private sealed record AxisPlan(int SourceSize, int TargetSize, ImageFilter Filter, Tap[][] Taps);
    [ThreadStatic] private static AxisPlan? lastPlan;

    public static byte[] Sharpen(byte[] source, int size, int strength)
    {
        ValidateImage(source, size);
        if (strength is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(strength));
        if (strength == 0) return source;

        var result = new byte[source.Length];
        var amount = strength * 0.015F;
        // A 3×3 Gaussian unsharp mask. Ignore differences under two luma levels
        // to avoid amplifying small fluctuations. Clamp borders, not wraparound.
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var sum = 0;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                sum += source[Math.Clamp(y + dy, 0, size - 1) * size + Math.Clamp(x + dx, 0, size - 1)]
                    * (dy == 0 ? 2 : 1) * (dx == 0 ? 2 : 1);
            var index = y * size + x;
            var detail = source[index] - sum / 16F;
            result[index] = Math.Abs(detail) < 2 ? source[index]
                : ToByte(source[index] + amount * detail);
        }
        return result;
    }

    public static byte[] Resize(byte[] source, int sourceSize, int targetSize, ImageFilter filter)
    {
        ValidateImage(source, sourceSize);
        if (targetSize < 1) throw new ArgumentOutOfRangeException(nameof(targetSize));
        if (!Enum.IsDefined(filter)) throw new ArgumentOutOfRangeException(nameof(filter));
        if (sourceSize == targetSize) return source;

        var plan = lastPlan;
        if (plan is null || plan.SourceSize != sourceSize || plan.TargetSize != targetSize || plan.Filter != filter)
            lastPlan = plan = new AxisPlan(sourceSize, targetSize, filter, BuildAxis(sourceSize, targetSize, filter));

        var horizontal = new float[sourceSize * targetSize];
        for (var y = 0; y < sourceSize; y++)
        for (var x = 0; x < targetSize; x++)
        {
            var value = 0F;
            foreach (var tap in plan.Taps[x]) value += source[y * sourceSize + tap.Index] * tap.Weight;
            horizontal[y * targetSize + x] = value;
        }
        var result = new byte[checked(targetSize * targetSize)];
        for (var y = 0; y < targetSize; y++)
        for (var x = 0; x < targetSize; x++)
        {
            var value = 0F;
            foreach (var tap in plan.Taps[y]) value += horizontal[tap.Index * targetSize + x] * tap.Weight;
            result[y * targetSize + x] = ToByte(value);
        }
        return result;
    }

    private static Tap[][] BuildAxis(int sourceSize, int targetSize, ImageFilter filter)
    {
        var axis = new Tap[targetSize][];
        var ratio = sourceSize / (double)targetSize;
        var antialias = Math.Max(1, ratio);
        var radius = filter == ImageFilter.Bicubic ? 2 : 1;
        for (var x = 0; x < targetSize; x++)
        {
            var center = (x + 0.5) * ratio - 0.5;
            if (filter == ImageFilter.Nearest)
            {
                axis[x] = [new Tap(Math.Clamp((int)Math.Floor(center + 0.5), 0, sourceSize - 1), 1)];
                continue;
            }
            var start = (int)Math.Ceiling(center - radius * antialias);
            var end = (int)Math.Floor(center + radius * antialias);
            var weights = new Dictionary<int, double>();
            for (var i = start; i <= end; i++)
            {
                var index = Math.Clamp(i, 0, sourceSize - 1);
                var weight = Kernel((center - i) / antialias, filter);
                weights[index] = weights.GetValueOrDefault(index) + weight;
            }
            var total = weights.Values.Sum();
            axis[x] = weights.Select(pair => new Tap(pair.Key, (float)(pair.Value / total))).ToArray();
        }
        return axis;
    }

    private static double Kernel(double value, ImageFilter filter)
    {
        var x = Math.Abs(value);
        return filter switch
        {
            // Catmull–Rom bicubic, a = -0.5.
            ImageFilter.Bicubic when x < 1 => (1.5 * x - 2.5) * x * x + 1,
            ImageFilter.Bicubic when x < 2 => ((-0.5 * x + 2.5) * x - 4) * x + 2,
            _ => 0
        };
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
    private static void ValidateImage(byte[] source, int size)
    {
        if (size < 1 || source.Length != checked(size * size))
            throw new ArgumentException("Expected a square grayscale image matching the specified size.");
    }
}
