namespace IRCamera;

/// <summary>The only hardware boundary used by the viewer; implementations belong to one apartment thread.</summary>
internal interface ICameraBackend : IDisposable
{
    event Action<CameraFrame>? FrameReceived;
    CameraDevice Open();
    byte[] ReadCalibration();
    bool ReadAutoCalibration();
    void SetAutoCalibration(bool enabled);
}

internal sealed class DirectShowCameraBackend(SessionDiagnostics diagnostics) : ICameraBackend
{
    private CameraCapture? capture;
    private string? devicePath;
    public event Action<CameraFrame>? FrameReceived;

    public CameraDevice Open()
    {
        if (capture is not null) throw new InvalidOperationException("Camera session is already open.");
        var device = CameraDeviceSelector.Select(CameraCapture.EnumerateDevices());
        devicePath = device.Path;
        var candidate = new CameraCapture(diagnostics);
        candidate.FrameReceived += frame => FrameReceived?.Invoke(frame);
        try { candidate.Start(device.Path); capture = candidate; }
        catch { candidate.Dispose(); devicePath = null; throw; }
        return device;
    }

    private CameraExtension Controls() => new(diagnostics.Record,
        devicePath ?? throw new InvalidOperationException("Camera session is not open."));

    public byte[] ReadCalibration() { using var controls = Controls(); return controls.ReadCalibration(); }
    public bool ReadAutoCalibration() { using var controls = Controls(); return controls.ReadAutoCalibration(); }
    public void SetAutoCalibration(bool enabled) { using var controls = Controls(); controls.SetAutoCalibration(enabled); }

    public void Dispose()
    {
        var owned = capture;
        capture = null;
        devicePath = null;
        owned?.Dispose();
    }
}

/// <summary>
/// Serializes hardware operations on a dedicated, message-pumping STA thread.
/// A timeout stops accepting work; it never aborts a COM call or reuses that session.
/// </summary>
internal sealed class CameraSession
{
    private readonly TaskCompletionSource<Control> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SessionDiagnostics diagnostics;
    private readonly TimeSpan operationTimeout;
    private ApplicationContext? context;
    private ICameraBackend? backend;
    private int stopping;
    private int faulted;
    public event Action<CameraFrame>? FrameReceived;
    public Task Completion => completed.Task;
    public bool IsFaulted => Volatile.Read(ref faulted) != 0;

    public CameraSession(SessionDiagnostics diagnostics, Func<ICameraBackend>? factory = null, TimeSpan? operationTimeout = null)
    {
        this.diagnostics = diagnostics;
        this.operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(8);
        factory ??= () => new DirectShowCameraBackend(diagnostics);
        var thread = new Thread(() => Run(factory)) { IsBackground = true, Name = "GW192A camera session" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Run(Func<ICameraBackend> factory)
    {
        try
        {
            using var dispatcher = new Control();
            _ = dispatcher.Handle;
            using var application = new ApplicationContext();
            context = application;
            backend = factory();
            backend.FrameReceived += frame =>
            {
                if (Volatile.Read(ref stopping) == 0 && !IsFaulted) FrameReceived?.Invoke(frame);
            };
            ready.TrySetResult(dispatcher);
            Application.Run(application);
        }
        catch (Exception ex) { diagnostics.Error("Camera thread", ex); ready.TrySetException(ex); }
        finally
        {
            try { backend?.Dispose(); }
            catch (Exception ex) { diagnostics.Error("Camera cleanup", ex); }
            diagnostics.Record("Camera session stopped.");
            completed.TrySetResult();
        }
    }

    private async Task<T> Invoke<T>(string operation, Func<ICameraBackend, T> action)
    {
        if (Volatile.Read(ref stopping) != 0 || IsFaulted) throw new InvalidOperationException("Camera session is closing or faulted.");
        diagnostics.Record($"{operation} started.");
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var dispatcher = await ready.Task.WaitAsync(operationTimeout).ConfigureAwait(false);
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = result.Task.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref stopping) != 0 || IsFaulted)
                {
                    result.TrySetException(new OperationCanceledException("Camera session was closed before this command started."));
                    return;
                }
                try { result.TrySetResult(action(backend!)); }
                catch (Exception ex)
                {
                    if (IsFaulted) diagnostics.Error($"{operation} returned after timeout", ex);
                    result.TrySetException(ex);
                }
            });
            var remaining = operationTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Camera operation deadline expired.");
            var value = await result.Task.WaitAsync(remaining).ConfigureAwait(false);
            diagnostics.Record($"{operation} completed.");
            return value;
        }
        catch (Exception ex)
        {
            if (ex is TimeoutException) Interlocked.Exchange(ref faulted, 1);
            diagnostics.Error(operation, ex);
            throw;
        }
    }

    public Task<CameraDevice> OpenAsync() => Invoke("Open camera", camera => camera.Open());
    public Task<byte[]> ReadCalibrationAsync() => Invoke("Read factory calibration", camera => camera.ReadCalibration());
    public Task<bool> ReadAutoCalibrationAsync() => Invoke("Read automatic calibration", camera => camera.ReadAutoCalibration());
    public Task<bool> SetAutoCalibrationAsync(bool enabled) => Invoke("Set automatic calibration", camera =>
    {
        camera.SetAutoCalibration(enabled);
        var confirmed = camera.ReadAutoCalibration();
        if (confirmed != enabled) throw new IOException("Automatic calibration readback did not match the requested mode.");
        return confirmed;
    });

    public async Task<bool> StopAsync(TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(3);
        if (Interlocked.Exchange(ref stopping, 1) == 0)
        {
            // Queue cleanup even if callers time out waiting for it. Never run COM
            // cleanup on the UI thread or create another session over a stuck one.
            _ = ready.Task.ContinueWith(task =>
            {
                if (!task.IsCompletedSuccessfully) return;
                try { task.Result.BeginInvoke(() => context?.ExitThread()); }
                catch (InvalidOperationException) { }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        try { await Completion.WaitAsync(budget).ConfigureAwait(false); return true; }
        catch (TimeoutException)
        {
            diagnostics.Record("Camera shutdown is still waiting for a native operation. Reconnect must wait for this session to finish.");
            return false;
        }
    }
}
