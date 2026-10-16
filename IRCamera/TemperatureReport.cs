namespace IRCamera;

internal static class TemperatureReport
{
    public static string Format(TemperatureFrame frame, TemperatureSettings settings, bool frozen, Point? measurementPoint = null)
        => string.Join(Environment.NewLine,
            $"GW192A · {(frozen ? "Frozen" : "Live")} temperature readings",
            $"Minimum: {Value(frame.Minimum, frame.QualityAt(frame.MinimumPosition))} · pixel ({frame.MinimumPosition.X}, {frame.MinimumPosition.Y})",
            measurementPoint is Point point && point != new Point(48, 48)
                ? $"Spot: {Value(frame.TemperatureAt(point), frame.QualityAt(point))} · pixel ({point.X}, {point.Y})"
                : $"Center: {Value(frame.Center, frame.QualityAt(new Point(48, 48)))}",
            $"Maximum: {Value(frame.Maximum, frame.QualityAt(frame.MaximumPosition))} · pixel ({frame.MaximumPosition.X}, {frame.MaximumPosition.Y})",
            $"Average: {Value(frame.Average)}{(frame.LimitedCount > 0 ? " (includes range-limited values)" : "")}",
            $"Emissivity: {settings.Emissivity:F2} · Distance: {settings.Distance:F2} m",
            $"Ambient: {settings.Ambient:F1} °C · Reflected: {settings.Reflected:F1} °C · Humidity: {settings.Humidity:P0}",
            $"Invalid pixels: {frame.InvalidCount} · Range-limited pixels: {frame.LimitedCount}");

    private static string Value(float value, MeasurementQuality quality = MeasurementQuality.Valid)
        => TemperatureText.Format(value, quality, "Unavailable");
}
