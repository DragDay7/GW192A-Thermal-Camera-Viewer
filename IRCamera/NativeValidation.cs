using System.Runtime.InteropServices;
using DirectShowLib;

namespace IRCamera;

internal static class NativeValidation
{
    public static void Capabilities(int count, int size)
    {
        if (count is < 1 or > 4096 || size < Marshal.SizeOf<VideoStreamConfigCaps>() || size > 65536)
            throw new InvalidDataException($"Invalid camera capability dimensions: {count} entries, {size} bytes each.");
    }

    public static VideoInfoHeader VideoInfo(AMMediaType type)
    {
        if (type.majorType != MediaType.Video || type.formatType != FormatType.VideoInfo
            || type.formatPtr == IntPtr.Zero || type.formatSize < Marshal.SizeOf<VideoInfoHeader>() || type.formatSize > 65536)
            throw new InvalidDataException("The camera returned an invalid video-format structure.");
        return Marshal.PtrToStructure<VideoInfoHeader>(type.formatPtr)!;
    }
}
