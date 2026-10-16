using System.Runtime.InteropServices;
using DirectShowLib;

namespace IRCamera;

internal sealed record CameraDevice(string Name, string Path);
internal sealed record CameraFrame(byte[] Data, int Width, int Height, double Timestamp);

/// <summary>Captures native YUY2 bytes, before any color conversion or resizing.</summary>
internal sealed class CameraCapture : ISampleGrabberCB, IDisposable
{
    private IFilterGraph2? graph;
    private ICaptureGraphBuilder2? builder;
    private IBaseFilter? source;
    private IBaseFilter? grabberFilter;
    private IBaseFilter? renderer;
    private ISampleGrabber? grabber;
    private int width;
    private int height;
    private volatile bool running;
    private readonly SessionDiagnostics diagnostics;

    public CameraCapture(SessionDiagnostics? diagnostics = null) => this.diagnostics = diagnostics ?? new SessionDiagnostics();

    public event Action<CameraFrame>? FrameReceived;

    public static IReadOnlyList<CameraDevice> EnumerateDevices()
    {
        var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        try
        {
            return devices.Select(d => new CameraDevice(d.Name, d.DevicePath)).ToArray();
        }
        finally
        {
            foreach (var device in devices) device.Dispose();
        }
    }

    public void Start(string devicePath, int requestedHeight = 176)
    {
        if (requestedHeight is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(requestedHeight));
        if (graph is not null) throw new InvalidOperationException("Camera is already open.");
        try
        {
            graph = (IFilterGraph2)new FilterGraph();
            builder = (ICaptureGraphBuilder2)new CaptureGraphBuilder2();
            Check(builder.SetFiltergraph(graph));
            var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            try
            {
                var device = devices.FirstOrDefault(d => d.DevicePath == devicePath)
                    ?? throw new InvalidOperationException("The camera was disconnected.");
                var iid = typeof(IBaseFilter).GUID;
                device.Mon.BindToObject(null!, null!, ref iid, out var instance);
                try { source = (IBaseFilter)instance; }
                catch { Release(ref instance); throw; }
            }
            finally
            {
                foreach (var device in devices) device.Dispose();
            }
            Check(graph.AddFilter(source, "GW192A"));
            Check(builder.FindInterface(PinCategory.Capture, MediaType.Video, source,
                typeof(IAMStreamConfig).GUID, out var configObject));
            try
            {
                var config = (IAMStreamConfig)configObject;
                Check(config.GetNumberOfCapabilities(out var count, out var size));
                NativeValidation.Capabilities(count, size);
                var caps = Marshal.AllocCoTaskMem(size);
                try
                {
                    var found = false;
                    for (var i = 0; i < count; i++)
                    {
                        Check(config.GetStreamCaps(i, out var type, caps));
                        try
                        {
                            if (type.subType != MediaSubType.YUY2 || type.formatType != FormatType.VideoInfo) continue;
                            var info = NativeValidation.VideoInfo(type);
                            if (info.BmiHeader.Width != 96 || Math.Abs((long)info.BmiHeader.Height) != requestedHeight) continue;
                            info.AvgTimePerFrame = 400_000;
                            Marshal.StructureToPtr(info, type.formatPtr, false);
                            Check(config.SetFormat(type));
                            width = 96;
                            height = requestedHeight;
                            found = true;
                            break;
                        }
                        finally { DsUtils.FreeAMMediaType(type); }
                    }
                    if (!found) throw new NotSupportedException($"Native YUY2 96×{requestedHeight} is not available.");
                }
                finally { Marshal.FreeCoTaskMem(caps); }
            }
            finally { Release(ref configObject); }

            grabberFilter = (IBaseFilter)new SampleGrabber();
            grabber = (ISampleGrabber)grabberFilter;
            var desired = new AMMediaType { majorType = MediaType.Video, subType = MediaSubType.YUY2, formatType = FormatType.VideoInfo };
            try { Check(grabber.SetMediaType(desired)); }
            finally { DsUtils.FreeAMMediaType(desired); }
            Check(graph.AddFilter(grabberFilter, "Native payload"));
            renderer = (IBaseFilter)new NullRenderer();
            Check(graph.AddFilter(renderer, "Null renderer"));
            Check(grabber.SetBufferSamples(false));
            Check(grabber.SetOneShot(false));
            Check(grabber.SetCallback(this, 1));
            // Explicitly connect pins: never allow an automatic color converter to alter payload bytes.
            IPin? sourcePin = null, inputPin = null, outputPin = null, sinkPin = null;
            try
            {
                sourcePin = DsFindPin.ByCategory(source, PinCategory.Capture, 0)
                    ?? throw new InvalidOperationException("The camera has no capture pin.");
                inputPin = DsFindPin.ByDirection(grabberFilter, PinDirection.Input, 0)
                    ?? throw new InvalidOperationException("The sample grabber has no input pin.");
                outputPin = DsFindPin.ByDirection(grabberFilter, PinDirection.Output, 0)
                    ?? throw new InvalidOperationException("The sample grabber has no output pin.");
                sinkPin = DsFindPin.ByDirection(renderer, PinDirection.Input, 0)
                    ?? throw new InvalidOperationException("The renderer has no input pin.");
                Check(graph.ConnectDirect(sourcePin, inputPin, null));
                Check(graph.ConnectDirect(outputPin, sinkPin, null));
            }
            finally
            {
                Release(ref sourcePin);
                Release(ref inputPin);
                Release(ref outputPin);
                Release(ref sinkPin);
            }
            var connected = new AMMediaType();
            try
            {
                Check(grabber.GetConnectedMediaType(connected));
                var info = NativeValidation.VideoInfo(connected);
                if (connected.subType != MediaSubType.YUY2 || info.BmiHeader.Width != width || Math.Abs((long)info.BmiHeader.Height) != height)
                    throw new NotSupportedException("The capture graph changed the native camera format.");
            }
            finally { DsUtils.FreeAMMediaType(connected); }
            running = true;
            Check(((IMediaControl)graph).Run());
        }
        catch { Dispose(); throw; }
    }

    public int BufferCB(double sampleTime, IntPtr buffer, int length)
    {
        if (!running) return 0;
        if (length != width * height * 2 || buffer == IntPtr.Zero)
        {
            diagnostics.FrameRejected();
            return 0;
        }
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, length);
            diagnostics.FrameReceived();
            FrameReceived?.Invoke(new CameraFrame(bytes, width, height, sampleTime));
        }
        catch (Exception ex) { diagnostics.CallbackFailed(ex); }
        return 0;
    }

    public int SampleCB(double sampleTime, IMediaSample sample) => 0;

    public void Dispose()
    {
        running = false;
        try { if (graph is IMediaControl control) Check(control.Stop()); }
        catch (Exception ex) { diagnostics.Error("Stop capture", ex); }
        try { if (grabber is not null) Check(grabber.SetCallback(null, 1)); }
        catch (Exception ex) { diagnostics.Error("Remove capture callback", ex); }
        grabber = null; // Same COM identity as grabberFilter; release it only once.
        Release(ref renderer);
        Release(ref grabberFilter);
        Release(ref source);
        Release(ref builder);
        Release(ref graph);
    }

    private void Release<T>(ref T? value) where T : class
    {
        var owned = value;
        value = null;
        try { if (owned is not null && Marshal.IsComObject(owned)) Marshal.ReleaseComObject(owned); }
        catch (Exception ex) { diagnostics.Error($"Release {typeof(T).Name}", ex); }
    }

    private static void Check(int hr) => DsError.ThrowExceptionForHR(hr);
}
