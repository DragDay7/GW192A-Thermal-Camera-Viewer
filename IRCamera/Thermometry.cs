using System.Buffers.Binary;

namespace IRCamera;

internal sealed record TemperatureSettings(float Emissivity = 0.97F, float Distance = 1F,
    float Ambient = 20F, float Reflected = 20F, float Humidity = 0.6F)
{
    public void Validate()
    {
        if (!float.IsFinite(Emissivity) || Emissivity is < 0.01F or > 1F
            || !float.IsFinite(Distance) || Distance is < 0F or > 100F
            || !float.IsFinite(Ambient) || Ambient is < -40F or > 80F
            || !float.IsFinite(Reflected) || Reflected is < -40F or > 500F
            || !float.IsFinite(Humidity) || Humidity is < 0F or > 1F)
            throw new ArgumentOutOfRangeException(nameof(TemperatureSettings), "Invalid thermometry settings.");
    }
}

internal sealed record SensorTelemetry(ushort[] Words, float? ReportedAmbient, float? ReportedHumidity)
{
    public bool IsCalibrating => Words.Length >= 8 && ((Words[6] & 1) != 0 || (Words[7] & 1) != 0);
}
internal enum MeasurementQuality : byte { Valid, Invalid, BelowRange, AboveRange }

internal sealed record TemperatureFrame(float[] Values, float Minimum, float Center, float Maximum,
    float Average, int InvalidCount, int LimitedCount, Point MinimumPosition, Point MaximumPosition,
    MeasurementQuality[]? Quality = null)
{
    public float TemperatureAt(Point point)
    {
        if (point.X is < 0 or >= 96 || point.Y is < 0 or >= 96)
            throw new ArgumentOutOfRangeException(nameof(point));
        return Values[point.Y * 96 + point.X];
    }

    public MeasurementQuality QualityAt(Point point)
    {
        var value = TemperatureAt(point);
        return Quality is null ? (float.IsFinite(value) ? MeasurementQuality.Valid : MeasurementQuality.Invalid)
            : Quality[point.Y * 96 + point.X];
    }
}

/// <summary>
/// GW192A F0 thermometry: fixed-point factory calibration revision 0x0404,
/// calibration sensor family 87, standard integrated lens. No image processing
/// or display palette enters these calculations.
/// </summary>
internal sealed class Thermometry
{
    private const float Kelvin = 273.1499938964844F;
    private readonly byte[] calibration;

    public Thermometry(byte[] data)
    {
        if (data.Length != 0x3800) throw new InvalidDataException("Factory calibration must contain 14,336 bytes.");
        calibration = (byte[])data.Clone();
        if (I(0) != 0x0404 || I(4) != data.Length || I(8) != 87 || I(0x2b10) != 1)
            throw new NotSupportedException("Unsupported factory calibration format; expected fixed-point GW192A/F0 revision 0x0404.");
        if (I(0x2b18) != 1000)
            throw new NotSupportedException("Unsupported external-optics calibration.");
        if (I(0x2bd0) > I(0x2bc0))
            throw new InvalidDataException("Factory heat-compensation bounds are reversed.");
        var populated = 0;
        for (var gain = 0; gain < 3; gain++)
        for (var range = 0; range < 5; range++)
        {
            var index = gain * 5 + range;
            var planck = 0x654 + index * 68;
            var baseFit = 0xcf8 + index * 36;
            if (I(planck) == 0 && I(planck + 4) == 0 && I(baseFit + 4) == 0) continue;
            if (I(planck) <= 0 || I(planck + 4) <= 0 || I(baseFit + 4) <= 0
                || Q8(0x50 + gain * 4) <= -Kelvin || Q8(0x64 + gain * 4) <= Q8(0x50 + gain * 4)
                || I(0x26c + gain * 20 + range * 4) / 50F <= -Kelvin)
                throw new InvalidDataException($"Factory calibration bank {gain}/{range} contains invalid physical coefficients.");
            populated++;
        }
        if (populated == 0) throw new InvalidDataException("Factory calibration contains no populated measurement banks.");
    }

    private int I(int offset) => BinaryPrimitives.ReadInt32LittleEndian(calibration.AsSpan(offset));
    private long L(int offset) => BinaryPrimitives.ReadInt64LittleEndian(calibration.AsSpan(offset));
    private float Q8(int offset) => I(offset) * (1F / 256);
    private float Q14(int offset) => I(offset) * (1F / 16384);

    public TemperatureFrame Measure(ThermalFrame frame, TemperatureSettings settings)
    {
        if (frame.Raw.Length != ThermalDecoder.PixelCount)
            throw new InvalidDataException("Thermometry requires a complete 96×96 sensor plane.");
        var curve = Prepare(frame, settings);
        var values = new float[frame.Raw.Length];
        var quality = new MeasurementQuality[values.Length];
        var table = new Dictionary<ushort, (float Value, MeasurementQuality Quality)>();
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        var minimumIndex = 0;
        var maximumIndex = 0;
        double sum = 0;
        var valid = 0;
        var limited = 0;
        for (var i = 0; i < values.Length; i++)
        {
            var raw = frame.Raw[i];
            if (!table.TryGetValue(raw, out var reading))
                table[raw] = reading = Convert(raw, curve, settings);
            values[i] = reading.Value;
            quality[i] = reading.Quality;
            if (!float.IsFinite(reading.Value)) continue;
            // Keep the first native pixel on ties for deterministic marker positions.
            if (reading.Value < minimum) { minimum = reading.Value; minimumIndex = i; }
            if (reading.Value > maximum) { maximum = reading.Value; maximumIndex = i; }
            sum += reading.Value;
            valid++;
            if (reading.Quality is MeasurementQuality.BelowRange or MeasurementQuality.AboveRange) limited++;
        }
        if (valid == 0) throw new InvalidDataException("The camera supplied no valid radiometric pixels.");
        return new TemperatureFrame(values, minimum, values[48 * 96 + 48], maximum,
            (float)(sum / valid), values.Length - valid, limited,
            new Point(minimumIndex % 96, minimumIndex / 96), new Point(maximumIndex % 96, maximumIndex / 96), quality);
    }

    internal sealed record Curve(int BaseGray, float Rate1, float Rate2, float R, float B, float O,
        float F, float Low, float High, float Transmission);

    internal Curve Prepare(ThermalFrame frame, TemperatureSettings settings)
    {
        settings.Validate();
        if (frame.SensorType != 210) throw new InvalidDataException("Unsupported sensor family for GW192A thermometry.");
        var telemetry = frame.Telemetry ?? throw new InvalidDataException("Missing radiometric frame metadata.");
        var words = telemetry.Words;
        if (words.Length < 96) throw new InvalidDataException("Incomplete radiometric frame metadata.");
        if (telemetry.IsCalibrating)
            throw new InvalidDataException("Camera calibration/freeze is in progress.");
        var range = words[5] & 0x3fff;
        var gain = words[13] & 0x3fff;
        if (range >= 5 || gain >= 3) throw new InvalidDataException("Unsupported camera range or gain index.");
        var index = gain * 5 + range;
        var tempScale = I(0x2b10) / 50F;
        var tempOffset = I(0x2b14) / 50F;
        float SensorTemp(ushort word)
        {
            var value = word & 0x3fff;
            if ((word & 0x2000) != 0) value = 0x2000 - value;
            return tempOffset + tempScale * value;
        }
        var cavity = SensorTemp(words[1]);
        var zeroCavity = SensorTemp(words[27]);
        var calibratedCavity = I(0x26c + gain * 20 + range * 4) / 50F;
        var calibratedZero = I(0x460 + gain * 20 + range * 4) / 50F;
        var delta = cavity - calibratedCavity;
        var responseOffset = 0x24d0 + index * 64;
        var normalizedDelta = delta / 10000F;
        var response1 = (float)L(responseOffset) * (1F / 4294967296F);
        var response2 = (float)L(responseOffset + 8) * (1F / 4294967296F);
        var response3 = (float)L(responseOffset + 16) * (1F / 4294967296F);
        var response4 = (float)L(responseOffset + 24) * (1F / 4294967296F);
        var rate1 = normalizedDelta * normalizedDelta * response2 + normalizedDelta * response1 + 1F;
        var rate2 = normalizedDelta * normalizedDelta * response4 + normalizedDelta * response3;
        if (!float.IsFinite(rate1) || !float.IsFinite(rate2) || rate1 is < 0.01F or > 5F)
            throw new InvalidDataException("Sensor temperature is outside the response calibration.");
        if (Math.Abs(rate2) < 1.1920929e-7F) rate2 = 0;

        var planck = 0x654 + index * 68;
        var baseFit = 0xcf8 + index * 36;
        var offset = I(baseFit) * (1F / 65536);
        var scale = I(baseFit + 4) * (1F / 1048576);
        var alternateBase = Q14(baseFit + 8);
        var low = Q8(0x50 + gain * 4);
        var high = Q8(0x64 + gain * 4);
        var r = Q8(planck) * scale;
        var b = Q14(planck + 4);
        var absoluteO = scale * Q8(planck + 8) + offset;
        var f = Q8(planck + 12);
        if (r <= 0 || b <= 0 || scale <= 0 || high <= low)
            throw new InvalidDataException("The camera selected an uncalibrated measurement range.");
        var baseTemperature = alternateBase != 0 && high >= 1100 ? alternateBase : calibratedCavity;
        PositiveFinite(baseTemperature + Kelvin, "reference absolute temperature");
        var denominator = (float)(Math.Exp(b / (baseTemperature + Kelvin)) - f);
        PositiveFinite(denominator, "calibrated Planck denominator");
        var calibratedGray = GrayInteger(absoluteO + r / denominator);
        var o = absoluteO - calibratedGray;
        var sensorKelvin = cavity + (baseTemperature - calibratedCavity) + Kelvin;
        PositiveFinite(sensorKelvin, "sensor absolute temperature");
        var sensorDenominator = (float)(Math.Exp(b / sensorKelvin) - f);
        PositiveFinite(sensorDenominator, "sensor Planck denominator");
        var cavityGray = GrayInteger(r / sensorDenominator + o);
        var revisedGray = GrayInteger(rate1 * cavityGray + rate2 * cavityGray * cavityGray);

        var hep = 0x1e8c + index * 32;
        var baseGray = GrayInteger(calibratedGray - (Q14(hep) + delta * Q14(hep + 16)) * revisedGray);
        var calibratedRise = calibratedCavity - calibratedZero;
        var rise = Math.Clamp(cavity - settings.Ambient, I(0x2bd0) / 50F, I(0x2bc0) / 50F);
        var split = Q14(hep + 4);
        var left = Q14(hep + 8);
        var right = Q14(hep + 12);
        float heatCompensation;
        if (calibratedRise <= split)
            heatCompensation = rise <= split ? (rise - calibratedRise) * left
                : (split - calibratedRise) * left + (rise - split) * right;
        else
            heatCompensation = rise > split ? (rise - calibratedRise) * right
                : (rise - split) * left + (split - calibratedRise) * right;
        if (heatCompensation is < -1000 or > 1000)
            throw new InvalidDataException("Sensor warm-up is outside the calibrated compensation range.");
        var zeroCompensation = Math.Clamp(GrayInteger((cavity - zeroCavity) * Q14(hep + 24)), -600, 600);
        baseGray -= (int)heatCompensation + zeroCompensation;
        if (baseGray is < 0 or > 16383) throw new InvalidDataException("Invalid radiometric baseline.");
        return new Curve(baseGray, rate1, rate2, r, b, o, f, low, high, Transmission(gain, settings));
    }

    private float Transmission(int gain, TemperatureSettings settings)
    {
        var coefficients = 0x21ac + gain * 32;
        var distance = settings.Distance * 0.001F + I(coefficients + 8) / 100F * -0.001F;
        var waterDistance = distance * I(coefficients);
        var attenuationDistance = distance * I(coefficients + 4);
        if (waterDistance <= 1.1920929e-7F) attenuationDistance = -attenuationDistance;
        var absoluteDistance = waterDistance <= 1.1920929e-7F ? -waterDistance : waterDistance;
        attenuationDistance = Math.Min(attenuationDistance, 200);
        var t = settings.Ambient;
        var vapor = (t * 0.341154545545578F + 4.7773213386535645F)
            + t * t * 0.009551350958645344F + t * t * t * 0.0001643152063479647F
            + t * t * t * t * 2.4755597678449703e-6F;
        var co2 = Math.Exp(0.03F * -0.0654F);
        var water = (float)Math.Exp(vapor * settings.Humidity * (float)(co2 * absoluteDistance) * -0.015912804752588272F);
        float[] boundaries = [0.2F, 0.5F, 1, 2, 5, 10, 20, 50, 100, 200];
        float[] slopes = [-0.075F, -0.066667F, -0.048F, -0.04F, -0.020667F, -0.0106F, -0.0064F, -0.003267F, -0.00148F, -0.00073F];
        float[] intercepts = [1, 0.9983332753181458F, 0.9890000224113464F, 0.9810000061988831F,
            0.9423332810401917F, 0.8920000195503235F, 0.8500000238418579F, 0.7873333096504211F, 0.6980000138282776F, 0.623F];
        var band = Array.FindIndex(boundaries, boundary => attenuationDistance <= boundary);
        if (band < 0) band = boundaries.Length - 1;
        var dry = intercepts[band] + slopes[band] * (float)(Math.Exp(0.03F * -0.19F) * attenuationDistance);
        var aerosol = (float)Math.Exp(Math.Pow(0.056944601237773895, 1.600000023841858)
            * (-3.91F / 10000F) * absoluteDistance);
        var result = dry * water * aerosol;
        if (!float.IsFinite(result) || result is <= 0 or > 1)
            throw new InvalidDataException("Atmospheric compensation is outside the supported range.");
        return waterDistance <= 1.1920929e-7F ? (1 - result) * 1.5F + 1 : result;
    }

    private static void PositiveFinite(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0) throw new InvalidDataException($"Invalid {name} in radiometric conversion.");
    }

    private static int GrayInteger(float value)
    {
        if (!float.IsFinite(value) || value < int.MinValue || value >= 2147483648F)
            throw new InvalidDataException("Calibration produced an invalid intermediate sensor count.");
        return (int)value;
    }

    private static (float Value, MeasurementQuality Quality) Convert(ushort raw, Curve curve, TemperatureSettings settings)
    {
        if (raw >= 0x4000) return (float.NaN, MeasurementQuality.Invalid);
        var delta = (float)(raw - curve.BaseGray);
        var corrected = curve.Rate2 == 0 ? delta / curve.Rate1
            : (MathF.Sqrt(curve.Rate1 * curve.Rate1 + 4 * curve.Rate2 * delta) - curve.Rate1) / (2 * curve.Rate2);
        if (!float.IsFinite(corrected) || corrected < int.MinValue || corrected >= 2147483648F) return (float.NaN, MeasurementQuality.Invalid);
        corrected = (int)corrected;
        if (corrected <= curve.O) return (float.NaN, MeasurementQuality.Invalid);
        var argument = curve.F + curve.R / (corrected - curve.O);
        if (!float.IsFinite(argument) || argument <= 1) return (float.NaN, MeasurementQuality.Invalid);
        var blackbody = (float)(curve.B / Math.Log(argument)) - Kelvin;
        var quality = blackbody < curve.Low ? MeasurementQuality.BelowRange
            : blackbody > curve.High ? MeasurementQuality.AboveRange : MeasurementQuality.Valid;
        blackbody = Math.Clamp(blackbody, curve.Low, curve.High);
        var objectK = blackbody + Kelvin;
        var ambientK = settings.Ambient + Kelvin;
        var reflectedK = settings.Reflected + Kelvin;
        var signal = objectK * objectK * objectK * objectK * 0.97F
            + ambientK * ambientK * ambientK * ambientK * 0.029999971389770508F;
        signal -= (curve.Transmission - settings.Emissivity * curve.Transmission)
            * reflectedK * reflectedK * reflectedK * reflectedK;
        signal -= (1 - curve.Transmission) * ambientK * ambientK * ambientK * ambientK;
        var value = MathF.Sqrt(MathF.Sqrt(signal / (settings.Emissivity * curve.Transmission))) - Kelvin;
        return float.IsFinite(value) ? (value, quality) : (float.NaN, MeasurementQuality.Invalid);
    }
}
