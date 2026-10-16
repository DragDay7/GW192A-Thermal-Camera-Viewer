using System.Buffers.Binary;
using System.Text.Json;
using IRCamera;

internal static class ThermometryRegression
{
    public static void Run(Action<bool, string> assert)
    {
        // Required committed fixture: a missing file is a failure, never a silent skip.
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "thermometry.json")));
        var fixture = document.RootElement;
        var calibration = new byte[0x3800];
        foreach (var word in fixture.GetProperty("CalibrationWords").EnumerateObject())
            BinaryPrimitives.WriteInt32LittleEndian(calibration.AsSpan(Convert.ToInt32(word.Name, 16)), word.Value.GetInt32());
        var words = new ushort[96];
        foreach (var word in fixture.GetProperty("TelemetryWords").EnumerateObject()) words[int.Parse(word.Name)] = word.Value.GetUInt16();
        var rawSamples = fixture.GetProperty("RawSamples").EnumerateArray().Select(value => value.GetUInt16()).ToArray();
        var converter = new Thermometry(calibration);
        ThermalFrame Frame(ushort raw) => new(new byte[9216], Enumerable.Repeat(raw, 9216).ToArray(), 210,
            raw, raw, raw, 0, new SensorTelemetry((ushort[])words.Clone(), null, null));
        var cases = fixture.GetProperty("Cases").EnumerateArray().ToArray();
        assert(cases.Length >= 3, "mandatory thermometry fixture covers multiple conversion settings");
        foreach (var item in cases)
        {
            var settings = new TemperatureSettings(item.GetProperty("Emissivity").GetSingle(), item.GetProperty("Distance").GetSingle(),
                item.GetProperty("Ambient").GetSingle(), item.GetProperty("Reflected").GetSingle(), item.GetProperty("Humidity").GetSingle());
            var expected = item.GetProperty("Expected").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            assert(expected.Length == rawSamples.Length, "thermometry fixture has a result for every raw sample");
            for (var i = 0; i < rawSamples.Length; i++)
            {
                var measured = converter.Measure(Frame(rawSamples[i]), settings);
                assert(Math.Abs(measured.Center - expected[i]) < 0.005F,
                    $"thermometry regression, emissivity {settings.Emissivity}, raw {rawSamples[i]}: {measured.Center:F5} °C");
            }
        }

        var sample = Frame(1600);
        sample.Raw[0] = 1001;
        sample.Raw[1] = 10000;
        sample.Raw[2] = 0xFFFF;
        var result = converter.Measure(sample, new TemperatureSettings());
        assert(result.TemperatureAt(result.MinimumPosition) == result.Minimum
            && result.TemperatureAt(result.MaximumPosition) == result.Maximum,
            "extrema positions identify the lowest and highest measured temperatures");
        var uniform = converter.Measure(Frame(1600), new TemperatureSettings());
        assert(uniform.MinimumPosition == Point.Empty && uniform.MaximumPosition == Point.Empty,
            "equal temperatures select deterministic first-pixel marker positions");
        assert(result.QualityAt(Point.Empty) == MeasurementQuality.BelowRange
            && result.QualityAt(new Point(1, 0)) == MeasurementQuality.AboveRange
            && result.QualityAt(new Point(2, 0)) == MeasurementQuality.Invalid
            && result.LimitedCount == 2 && result.InvalidCount == 1,
            "per-pixel quality distinguishes lower bounds, upper bounds, and invalid samples");
        assert(TemperatureText.Format(result.Values[0], result.QualityAt(Point.Empty)).StartsWith("≤")
            && TemperatureText.Format(result.Values[1], result.QualityAt(new Point(1, 0))).StartsWith("≥"),
            "range-limited values are visibly bounds rather than exact temperatures");
        assert(TemperatureReport.Format(result, new TemperatureSettings(), false, Point.Empty).Contains("Spot: ≤")
            && TemperatureReport.Format(result, new TemperatureSettings(), false).Contains("includes range-limited values"),
            "copied spot and average measurements preserve range-limit semantics");

        void Reject(byte[] data, string label, bool duringMeasurement = false)
        {
            try
            {
                var invalid = new Thermometry(data);
                if (duringMeasurement) invalid.Measure(Frame(1600), new TemperatureSettings());
            }
            catch (InvalidDataException) { assert(true, label); return; }
            throw new Exception($"Invalid calibration accepted: {label}");
        }
        var corrupt = (byte[])calibration.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(0x698), -1);
        Reject(corrupt, "negative response coefficient rejected before measuring");
        corrupt = (byte[])calibration.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(0x2bd0), 2000);
        Reject(corrupt, "reversed heat-compensation bounds rejected before measuring");
        corrupt = (byte[])calibration.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(0x50), -100000);
        Reject(corrupt, "calibration range below absolute zero rejected");
        corrupt = (byte[])calibration.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(0x6a4), int.MaxValue);
        Reject(corrupt, "invalid Planck denominator rejected before float-to-integer conversion", true);
        try { converter.Measure(Frame(1600) with { Raw = new ushort[1] }, new TemperatureSettings()); throw new Exception("Incomplete sensor plane accepted"); }
        catch (InvalidDataException) { assert(true, "thermometry rejects incomplete sensor arrays independently of the decoder"); }
        try { converter.Measure(Frame(0xFFFF), new TemperatureSettings()); throw new Exception("All-invalid sensor plane accepted"); }
        catch (InvalidDataException) { assert(true, "all-invalid sensor plane rejected"); }
        var paused = Frame(1600);
        paused.Telemetry!.Words[6] = 1;
        try { converter.Measure(paused, new TemperatureSettings()); throw new Exception("Calibration-in-progress frame accepted"); }
        catch (InvalidDataException) { assert(true, "camera calibration suppresses temperature values"); }
        Reject(new byte[16], "truncated factory calibration rejected");
        var unsupported = (byte[])calibration.Clone();
        unsupported[0] = 5;
        try { new Thermometry(unsupported); throw new Exception("Unsupported calibration accepted"); }
        catch (NotSupportedException) { assert(true, "unsupported calibration revision rejected"); }
        try { new TemperatureSettings(Emissivity: float.NaN).Validate(); throw new Exception("Non-finite settings accepted"); }
        catch (ArgumentException) { assert(true, "non-finite measurement settings rejected"); }
    }
}
