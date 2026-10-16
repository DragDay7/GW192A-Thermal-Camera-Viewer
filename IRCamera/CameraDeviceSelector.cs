namespace IRCamera;

internal static class CameraDeviceSelector
{
    public static CameraDevice Select(IReadOnlyList<CameraDevice> devices)
    {
        var matching = devices.Where(device => device.Path?.Contains("vid_2bdf&pid_0102", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        return matching.Length switch
        {
            0 => throw new InvalidOperationException("GW192A (USB 2BDF:0102) is not connected."),
            1 => matching[0],
            _ => throw new InvalidOperationException("More than one GW192A is connected. Leave one connected to avoid using another camera's calibration.")
        };
    }
}
