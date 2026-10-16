using System.Runtime.InteropServices;
using DirectShowLib;
using IRCamera;

internal static class CameraSessionRegression
{
    public static void Run(Action<bool, string> assert)
    {
        long now = 0;
        var retry = new CalibrationRetryPolicy(() => now);
        retry.Reset();
        assert(retry.TryBegin() && !retry.TryBegin(), "calibration retries do not overlap");
        retry.Complete(false);
        now = 999;
        assert(!retry.TryBegin(), "calibration retry respects its one-second backoff without sleeping");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            now += 1000;
            assert(retry.TryBegin(), "scheduled calibration retry becomes available");
            retry.Complete(false);
        }
        now += 1000;
        assert(retry.Remaining == 0 && !retry.TryBegin(), "calibration retries stop after five attempts");
        retry.Reset();
        retry.TryBegin();
        retry.Complete(false, permanentFailure: true);
        assert(!retry.TryBegin(), "unsupported calibration is not retried");
        retry.Reset();
        retry.TryBegin();
        retry.Complete(true);
        assert(!retry.TryBegin(), "successful calibration stops retries");
        retry.Reset();
        retry.Cancel();
        assert(!retry.TryBegin(), "disconnect cancels scheduled calibration work");
        var device = new CameraDevice("GW192A", "usb#vid_2bdf&pid_0102#camera-a");
        assert(CameraDeviceSelector.Select([device]) == device, "single camera identity selected once for capture and controls");
        foreach (var devices in new CameraDevice[][] { [], [device, device with { Path = "usb#vid_2bdf&pid_0102#camera-b" }] })
        {
            try { CameraDeviceSelector.Select(devices); throw new Exception("Ambiguous camera selection accepted"); }
            catch (InvalidOperationException) { assert(true, "missing or ambiguous camera selection is rejected"); }
        }
        foreach (var capability in new[] { (0, 128), (1, -1), (100000, 128), (1, 100000) })
        {
            try { NativeValidation.Capabilities(capability.Item1, capability.Item2); throw new Exception("Invalid capability accepted"); }
            catch (InvalidDataException) { assert(true, "native capability allocation bounds are validated"); }
        }
        foreach (var size in new[] { 0, 4, Marshal.SizeOf<VideoInfoHeader>() - 1 })
        {
            var format = new AMMediaType { majorType = MediaType.Video, formatType = FormatType.VideoInfo, formatPtr = new IntPtr(1), formatSize = size };
            try { NativeValidation.VideoInfo(format); throw new Exception("Undersized native structure accepted"); }
            catch (InvalidDataException) { assert(true, "video format is rejected before reading an undersized native pointer"); }
        }
        var missingPointer = new AMMediaType { majorType = MediaType.Video, formatType = FormatType.VideoInfo, formatSize = Marshal.SizeOf<VideoInfoHeader>() };
        try { NativeValidation.VideoInfo(missingPointer); throw new Exception("Null media pointer accepted"); }
        catch (InvalidDataException) { assert(true, "null media-format pointer is rejected before marshalling"); }
        var diagnostics = new SessionDiagnostics();
        for (var i = 0; i < 200; i++) diagnostics.Record($"event-{i}");
        diagnostics.CallbackFailed(new IOException("test callback"));
        assert(!diagnostics.Snapshot().Contains("event-0\n") && diagnostics.Snapshot().Contains("event-199")
            && diagnostics.Snapshot().Contains("callback failures: 1"), "Release diagnostics bound events and retain capture-error counters");
        diagnostics.Record(new string('Z', 10000));
        assert(!diagnostics.Snapshot().Contains(new string('Z', 5000)), "diagnostics cap individual event size as well as event count");

        var fake = new FakeBackend();
        var owner = new CameraSession(diagnostics, () => fake);
        var frames = 0;
        owner.FrameReceived += _ => frames++;
        try
        {
            owner.OpenAsync().GetAwaiter().GetResult();
            owner.ReadCalibrationAsync().GetAwaiter().GetResult();
            owner.SetAutoCalibrationAsync(false).GetAwaiter().GetResult();
            fake.EmitFrame();
            assert(frames == 1, "active session forwards camera frames");
            fake.CalibrationFailure = new IOException("temporary calibration read failure");
            try { owner.ReadCalibrationAsync().GetAwaiter().GetResult(); throw new Exception("Hardware failure was swallowed"); }
            catch (IOException) { assert(diagnostics.Snapshot().Contains("temporary calibration read failure"), "hardware errors propagate with Release diagnostic context"); }
            fake.CalibrationFailure = null;
            assert(owner.ReadCalibrationAsync().GetAwaiter().GetResult().Length == 3, "recoverable calibration read failure does not poison the session");
            assert(fake.Threads.Distinct().Count() == 1 && fake.Apartment == ApartmentState.STA
                && fake.Threads[0] != Environment.CurrentManagedThreadId,
                "open, calibration, and settings run on one dedicated STA, never the caller thread");
        }
        finally { assert(owner.StopAsync().GetAwaiter().GetResult(), "camera owner stops asynchronously"); }
        assert(fake.DisposeCount == 1 && fake.DisposeThread == fake.Threads[0], "camera cleanup runs once on its owning apartment");
        fake.EmitFrame();
        assert(frames == 1, "late callbacks from a stopped session cannot reach the viewer");
        assert(owner.StopAsync().GetAwaiter().GetResult() && fake.DisposeCount == 1, "repeated session shutdown is idempotent");

        using var release = new ManualResetEventSlim();
        var blocked = new FakeBackend { BlockCalibration = release };
        var timed = new CameraSession(diagnostics, () => blocked, TimeSpan.FromSeconds(1));
        try
        {
            timed.OpenAsync().GetAwaiter().GetResult();
            try { timed.ReadCalibrationAsync().GetAwaiter().GetResult(); throw new Exception("Blocked operation did not time out"); }
            catch (TimeoutException) { assert(timed.IsFaulted, "operation deadline faults the session without blocking or aborting the caller"); }
            try { timed.ReadAutoCalibrationAsync().GetAwaiter().GetResult(); throw new Exception("Faulted session accepted another command"); }
            catch (InvalidOperationException) { assert(true, "timed-out hardware session cannot be reused"); }
            assert(!timed.StopAsync(TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult() && blocked.DisposeCount == 0,
                "shutdown reports a blocked native call without cross-thread disposal");
        }
        finally
        {
            release.Set();
            timed.Completion.Wait(TimeSpan.FromSeconds(5));
        }
        assert(blocked.DisposeCount == 1 && blocked.DisposeThread == blocked.Threads[0],
            "timed-out operation is cleaned up on its original thread after it returns");
    }

    private sealed class FakeBackend : ICameraBackend
    {
        public event Action<CameraFrame>? FrameReceived;
        public List<int> Threads { get; } = [];
        public ApartmentState Apartment { get; private set; }
        public ManualResetEventSlim? BlockCalibration { get; init; }
        public Exception? CalibrationFailure { get; set; }
        public int DisposeCount { get; private set; }
        public int DisposeThread { get; private set; }
        private bool enabled = true;
        private void Record() { Threads.Add(Environment.CurrentManagedThreadId); Apartment = Thread.CurrentThread.GetApartmentState(); }
        public CameraDevice Open() { Record(); return new("test", "test-device"); }
        public byte[] ReadCalibration()
        {
            Record();
            BlockCalibration?.Wait(TimeSpan.FromSeconds(5));
            if (CalibrationFailure is not null) throw CalibrationFailure;
            return [1, 2, 3];
        }
        public void EmitFrame() => FrameReceived?.Invoke(new CameraFrame(new byte[33792], 96, 176, 0));
        public bool ReadAutoCalibration() { Record(); return enabled; }
        public void SetAutoCalibration(bool value) { Record(); enabled = value; }
        public void Dispose() { DisposeCount++; DisposeThread = Environment.CurrentManagedThreadId; }
    }
}
