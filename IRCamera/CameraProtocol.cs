namespace IRCamera;

internal static class CameraProtocol
{
    public static void Initialize(Func<int, byte[]> readControl)
    {
        // Discovery is required after a USB power cycle. Until selector 4 is
        // read, this camera exposes legacy 512-byte command controls instead
        // of the UVC 2.0 selector sizes used by the calibration protocol.
        var version = readControl(4);
        if (!version.AsSpan().SequenceEqual(new byte[] { (byte)'2', (byte)'.', (byte)'0', 0 }))
            throw new NotSupportedException("The camera did not negotiate the supported UVC control protocol (2.0).");
    }
}
