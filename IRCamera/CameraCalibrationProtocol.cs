namespace IRCamera;

/// <summary>UVC image-enhancement payload for the camera's auto-shutter switch.</summary>
internal static class CameraCalibrationProtocol
{
    // Automatic shutter mode is at wire offset 0x4e. Preserve the complete
    // camera response, including any additional image-setting fields.
    private const int AutoShutterOffset = 0x4e;

    public static bool IsEnabled(ReadOnlySpan<byte> settings)
    {
        if (settings.Length <= AutoShutterOffset)
            throw new NotSupportedException("Camera image settings do not expose automatic calibration.");
        return settings[AutoShutterOffset] switch
        {
            1 => false,
            2 => true,
            _ => throw new NotSupportedException("The camera reports an unsupported automatic-calibration mode.")
        };
    }

    public static byte[] WithAutoCalibration(byte[] settings, bool enabled)
    {
        _ = IsEnabled(settings);
        var updated = (byte[])settings.Clone();
        updated[AutoShutterOffset] = enabled ? (byte)2 : (byte)1;
        return updated;
    }
}
