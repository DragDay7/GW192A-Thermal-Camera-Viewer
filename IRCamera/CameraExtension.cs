using System.Runtime.InteropServices;
using System.Buffers.Binary;
using DirectShowLib;

namespace IRCamera;

/// <summary>Kernel-streaming access to the camera's advertised extension unit.</summary>
internal sealed class CameraExtension : IDisposable
{
    private readonly IBaseFilter source;
    private readonly object node;
    private readonly IKsControl control;
    private readonly int nodeId;
    private readonly Action<string>? trace;
    private bool disposed;
    private static readonly Guid PropertySet = new("a29e7641-de04-47e3-8b2b-f4341aff003b");

    public CameraExtension(Action<string>? trace = null, string? devicePath = null)
    {
        this.trace = trace;
        var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        try
        {
            devicePath ??= CameraDeviceSelector.Select(devices.Select(d => new CameraDevice(d.Name, d.DevicePath)).ToArray()).Path;
            var device = devices.FirstOrDefault(d => string.Equals(d.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The selected GW192A was disconnected.");
            var iid = typeof(IBaseFilter).GUID;
            device.Mon.BindToObject(null!, null!, ref iid, out var instance);
            try { source = (IBaseFilter)instance; }
            catch { Release(instance); throw; }
        }
        finally { foreach (var device in devices) device.Dispose(); }
        try
        {
            var topology = (IKsTopologyInfo)source;
            DsError.ThrowExceptionForHR(topology.get_NumNodes(out var count));
            for (var i = 0; i < count; i++)
            {
                DsError.ThrowExceptionForHR(topology.get_NodeType(i, out var type));
                if (type != new Guid("941C7AC0-C559-11D0-8A2B-00A0C9255AC1")) continue;
                var hr = topology.CreateNodeInstance(i, typeof(IKsControl).GUID, out var instance);
                if (hr < 0) continue;
                node = instance;
                control = (IKsControl)instance;
                nodeId = i;
                CameraProtocol.Initialize(Read);
                return;
            }
            throw new NotSupportedException("The camera exposes no accessible extension-unit node.");
        }
        catch
        {
            Release(node);
            Release(source);
            throw;
        }
    }

    private uint GetControlSize(int selector)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var request = new KsNodeProperty { Set = PropertySet, Id = (uint)selector, Flags = 0x10000001, NodeId = (uint)nodeId };
        var hr = control.KsProperty(ref request, 32, IntPtr.Zero, 0, out var required);
        trace?.Invoke($"GET size selector {selector}: hr=0x{hr:X8}, required={required}");
        if (hr < 0 && hr != unchecked((int)0x8007007A) && hr != unchecked((int)0x800700EA))
            Marshal.ThrowExceptionForHR(hr);
        if (required == 0 || required > 65536)
            throw new InvalidOperationException($"Selector {selector}: length query returned 0x{hr:X8}, length {required}.");
        return required;
    }

    private byte[] Read(int selector)
    {
        var required = GetControlSize(selector);
        var request = new KsNodeProperty { Set = PropertySet, Id = (uint)selector, Flags = 0x10000001, NodeId = (uint)nodeId };
        var data = Marshal.AllocHGlobal((int)required);
        try
        {
            var hr = control.KsProperty(ref request, 32, data, required, out var returned);
            trace?.Invoke($"GET selector {selector}: capacity={required}, hr=0x{hr:X8}, returned={returned}");
            Marshal.ThrowExceptionForHR(hr);
            if (returned == 0 || returned > required)
                throw new InvalidDataException($"Selector {selector} returned an invalid transfer size {returned} for capacity {required}.");
            var bytes = new byte[returned];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    // Protocol UVC 2.0: select GET_THERMOMETRY_CALIBRATION_FILE
    // operation (data selector 3, command 0x0e). This selects a read operation;
    // it does not write the calibration file.
    private void SelectCalibrationRead()
        => SelectOperation(3, 0x0e);

    private void SelectOperation(byte selector, byte command)
        => Write(5, [selector, command]);

    private void Write(int selector, byte[] bytes)
    {
        // Verify the negotiated selector size before SET as well as GET.
        var required = GetControlSize(selector);
        if (bytes.Length != required)
            throw new InvalidDataException($"Selector {selector} expects {required} bytes; refusing to write {bytes.Length} bytes.");
        var request = new KsNodeProperty { Set = PropertySet, Id = (uint)selector, Flags = 0x10000002, NodeId = (uint)nodeId };
        var data = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, data, bytes.Length);
            var hr = control.KsProperty(ref request, 32, data, (uint)bytes.Length, out var returned);
            trace?.Invoke($"SET selector {selector}: length={bytes.Length}, hr=0x{hr:X8}, returned={returned}");
            Marshal.ThrowExceptionForHR(hr);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    internal byte[] ReadImageEnhancement()
    {
        SelectOperation(2, 5);
        return Read(2);
    }

    public bool ReadAutoCalibration() => CameraCalibrationProtocol.IsEnabled(ReadImageEnhancement());

    public void SetAutoCalibration(bool enabled)
    {
        var settings = CameraCalibrationProtocol.WithAutoCalibration(ReadImageEnhancement(), enabled);
        SelectOperation(2, 5);
        Write(2, settings);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (ReadAutoCalibration() == enabled) return;
            Thread.Sleep(100);
        }
        throw new IOException("The camera did not confirm the automatic-calibration setting.");
    }

    public byte[] ReadCalibration()
    {
        SelectCalibrationRead();
        var response = Read(3);
        if (response.Length != 5 || response[0] != 1)
            throw new InvalidDataException("The camera returned an invalid calibration-length response.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(response.AsSpan(1));
        if (length != 0x3800 + 68)
            throw new InvalidDataException($"Unsupported calibration response length {length}.");
        var result = new byte[length];
        var offset = 0;
        for (var sequence = 1; offset < length; sequence++)
        {
            if (sequence > 32) throw new InvalidDataException("Calibration transfer exceeded its packet limit.");
            var packet = Read(3);
            if (packet.Length < 6 || packet[0] != 2
                || BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(1)) != sequence)
                throw new InvalidDataException("Calibration transfer packet is out of sequence.");
            var count = Math.Min(packet.Length - 5, length - offset);
            packet.AsSpan(5, count).CopyTo(result.AsSpan(offset));
            offset += count;
        }
        var fileLength = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(64));
        if (fileLength != result.Length - 68)
            throw new InvalidDataException("The calibration file length does not match its transfer header.");
        return result.AsSpan(68).ToArray();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Release(node);
        Release(source);
    }

    private void Release(object? instance)
    {
        try { if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance); }
        catch (Exception ex) { trace?.Invoke($"COM cleanup failed: 0x{ex.HResult:X8} {ex.Message}"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsNodeProperty
    {
        public Guid Set;
        public uint Id, Flags, NodeId, Reserved;
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsNodeProperty property, uint propertyLength, IntPtr data, uint dataLength, out uint returned);
        [PreserveSig] int KsMethod(IntPtr method, uint methodLength, IntPtr data, uint dataLength, out uint returned);
        [PreserveSig] int KsEvent(IntPtr ksEvent, uint eventLength, IntPtr data, uint dataLength, out uint returned);
    }
}
