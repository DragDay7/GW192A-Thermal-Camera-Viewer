namespace IRCamera;

internal static class TemperatureText
{
    public static string Format(float value, MeasurementQuality quality = MeasurementQuality.Valid, string unavailable = "— °C")
    {
        if (!float.IsFinite(value) || quality == MeasurementQuality.Invalid) return unavailable;
        var prefix = quality switch
        {
            MeasurementQuality.BelowRange => "≤",
            MeasurementQuality.AboveRange => "≥",
            _ => ""
        };
        return $"{prefix}{value:F1} °C";
    }
}
