using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using IRCamera;

/// <summary>Exercises the actual UI lifecycle with a scripted device, not private-field injection.</summary>
internal static class ViewerSessionRegression
{
    public static void Run(Action<bool, string> assert)
    {
        Exception? failure = null;
        var checks = new List<(bool Passed, string Name)>();
        var thread = new Thread(() =>
        {
            var cameras = new List<ScriptedCamera>();
            try
            {
                using var viewer = new MainForm(log => new CameraSession(log, () =>
                {
                    var camera = new ScriptedCamera();
                    lock (cameras) cameras.Add(camera);
                    return camera;
                }), showErrorDialogs: false) { ShowInTaskbar = false, Opacity = 0 };
                using var timer = new System.Windows.Forms.Timer { Interval = 30 };
                var deadline = Stopwatch.StartNew();
                var phase = 0;
                Rectangle? initialBounds = null;
                var canvas = viewer.Controls.Find("ThermalCanvas", true).Single();
                var connection = (Button)viewer.Controls.Find("ConnectCamera", true).Single();
                var automatic = (CheckBox)viewer.Controls.Find("AutoCalibration", true).Single();
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        if (deadline.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Scripted viewer lifecycle did not complete.");
                        if (phase == 0 && viewer.TemperatureFramesReceived >= 5 && connection.Enabled)
                        {
                            initialBounds = canvas.Bounds;
                            checks.Add((viewer.HasTemperatureFrame, "viewer recovers from an initial calibration read failure without reconnecting"));
                            automatic.Checked = false;
                            phase = 1;
                        }
                        else if (phase == 1 && automatic.Enabled && connection.Enabled)
                        {
                            lock (cameras) checks.Add((!cameras[^1].Automatic, "actual checkbox sends and confirms a serialized hardware setting"));
                            connection.PerformClick();
                            checks.Add((!viewer.HasTemperatureFrame, "disconnect clears actual temperatures before asynchronous cleanup completes"));
                            phase = 2;
                        }
                        else if (phase == 2 && connection.Enabled)
                        {
                            connection.PerformClick();
                            phase = 3;
                        }
                        else if (phase == 3 && viewer.TemperatureFramesReceived >= 5 && connection.Enabled)
                        {
                            checks.Add((viewer.HasTemperatureFrame && canvas.Bounds == initialBounds,
                                "actual viewer reconnect restores temperatures without changing viewport geometry"));
                            timer.Stop();
                            viewer.Close();
                        }
                    }
                    catch (Exception ex) { failure = ex; timer.Stop(); viewer.Close(); }
                };
                timer.Start();
                Application.Run(viewer);
                lock (cameras)
                    checks.Add((cameras.Count == 2 && cameras.All(camera => camera.DisposeCount == 1 && camera.OpenThread == camera.DisposeThread),
                        "both UI-created camera sessions dispose once on their owner threads"));
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(12))) throw new TimeoutException("Viewer lifecycle test thread did not stop.");
        if (failure is not null) throw new Exception("Viewer lifecycle regression failed", failure);
        foreach (var check in checks) assert(check.Passed, check.Name);
    }

    private sealed class ScriptedCamera : ICameraBackend
    {
        private readonly byte[] calibration = new byte[0x3800];
        private readonly byte[] frame = new byte[ThermalDecoder.PayloadByteCount];
        private System.Threading.Timer? timer;
        private int reads;
        public event Action<CameraFrame>? FrameReceived;
        public bool Automatic { get; private set; } = true;
        public int OpenThread { get; private set; }
        public int DisposeThread { get; private set; }
        public int DisposeCount { get; private set; }

        public ScriptedCamera()
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "thermometry.json")));
            foreach (var word in fixture.RootElement.GetProperty("CalibrationWords").EnumerateObject())
                BinaryPrimitives.WriteInt32LittleEndian(calibration.AsSpan(Convert.ToInt32(word.Name, 16)), word.Value.GetInt32());
            for (var i = 0; i < 9216; i++) BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(i * 2), 1600);
            foreach (var word in fixture.RootElement.GetProperty("TelemetryWords").EnumerateObject())
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(18432 + int.Parse(word.Name) * 2), word.Value.GetUInt16());
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(18432 + 192), 0xAABBCCDD);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(18432 + 196), 210);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(18432 + 200), 96);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(18432 + 204), 96);
        }

        public CameraDevice Open()
        {
            OpenThread = Environment.CurrentManagedThreadId;
            timer = new System.Threading.Timer(_ => FrameReceived?.Invoke(new CameraFrame((byte[])frame.Clone(), 96, 176, 0)), null, 0, 40);
            return new CameraDevice("scripted", "scripted-device");
        }
        public byte[] ReadCalibration()
        {
            if (reads++ == 0) throw new IOException("Scripted transient calibration failure");
            return (byte[])calibration.Clone();
        }
        public bool ReadAutoCalibration() => Automatic;
        public void SetAutoCalibration(bool enabled) => Automatic = enabled;
        public void Dispose()
        {
            timer?.Dispose();
            timer = null;
            DisposeCount++;
            DisposeThread = Environment.CurrentManagedThreadId;
        }
    }
}
