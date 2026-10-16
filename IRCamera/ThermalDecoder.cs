using System.Buffers.Binary;

namespace IRCamera;

internal sealed record ThermalFrame(byte[] Luminance, ushort[] Raw, int SensorType,
    int RawMinimum, int RawMaximum, int RawCenter, double Timestamp, SensorTelemetry? Telemetry = null);

/// <summary>GW192A/F0 native payload, verified against frames from USB 2BDF:0102.</summary>
internal static class ThermalDecoder
{
    public const int Width = 96;
    public const int Height = 96;
    public const int PixelCount = Width * Height;
    public const int RawByteCount = PixelCount * 2;
    public const int MetadataByteCount = 768;
    public const int ImageOffset = RawByteCount + MetadataByteCount;
    public const int PayloadByteCount = 96 * 176 * 2;

    public static ThermalFrame Decode(CameraFrame frame)
    {
        if (frame.Width != 96 || frame.Height != 176 || frame.Data.Length != PayloadByteCount)
            throw new InvalidDataException("Expected an intact YUY2 96×176 camera payload.");

        var data = frame.Data.AsSpan();
        var metadata = data.Slice(RawByteCount, MetadataByteCount);
        // The appended sensor record occupies the second 192-byte metadata row.
        if (BinaryPrimitives.ReadUInt32LittleEndian(metadata[192..]) != 0xAABBCCDD
            || BinaryPrimitives.ReadUInt32LittleEndian(metadata[200..]) != Width
            || BinaryPrimitives.ReadUInt32LittleEndian(metadata[204..]) != Height)
            throw new InvalidDataException("The camera payload has an unrecognized sensor header.");

        var sensor = BinaryPrimitives.ReadInt32LittleEndian(metadata[196..]);
        if (sensor != 210)
            throw new InvalidDataException($"Unsupported sensor type {sensor}; expected the F0 96×96 sensor (210).");

        var raw = new ushort[PixelCount];
        var minimum = ushort.MaxValue;
        ushort maximum = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
            raw[i] = value;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }

        // An independent 96×96 Y plane follows raw + metadata. Its 4:2:0 chroma
        // is not needed when applying a thermal palette to camera-processed luma.
        var luminance = data.Slice(ImageOffset, PixelCount).ToArray();
        var words = new ushort[96];
        for (var i = 0; i < words.Length; i++)
            words[i] = BinaryPrimitives.ReadUInt16LittleEndian(metadata[(i * 2)..]);
        var telemetry = new SensorTelemetry(words,
            BinaryPrimitives.ReadInt32LittleEndian(metadata[208..]) / 1000F,
            BinaryPrimitives.ReadInt32LittleEndian(metadata[212..]) / 100000F);
        return new ThermalFrame(luminance, raw, sensor, minimum, maximum,
            raw[(Height / 2) * Width + Width / 2], frame.Timestamp, telemetry);
    }
}
