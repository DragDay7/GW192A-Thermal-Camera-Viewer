namespace IRCamera;

/// <summary>Bounded, in-memory diagnostics. No frame data or automatic disk writes.</summary>
internal sealed class SessionDiagnostics
{
    private readonly Queue<string> events = new();
    private readonly object gate = new();
    private long received;
    private long rejected;
    private long callbackFailures;
    private long superseded;
    private long rendered;
    private double renderingMilliseconds;
    private double maximumRenderMilliseconds;

    public void Record(string message)
    {
        if (message.Length > 4096) message = message[..4096] + "…";
        lock (gate)
        {
            if (events.Count == 128) events.Dequeue();
            events.Enqueue($"{DateTimeOffset.Now:O} {message}");
        }
    }

    public void Error(string operation, Exception error)
        => Record($"{operation}: HRESULT 0x{error.HResult:X8}{Environment.NewLine}{error}");

    public void FrameReceived() => Interlocked.Increment(ref received);
    public void FrameRejected() => Interlocked.Increment(ref rejected);
    public void FrameSuperseded() => Interlocked.Increment(ref superseded);
    public void FrameRendered(TimeSpan elapsed)
    {
        lock (gate)
        {
            rendered++;
            renderingMilliseconds += elapsed.TotalMilliseconds;
            maximumRenderMilliseconds = Math.Max(maximumRenderMilliseconds, elapsed.TotalMilliseconds);
        }
    }
    public void CallbackFailed(Exception error)
    {
        // Avoid flooding the event log on every frame while retaining a counter.
        if (Interlocked.Increment(ref callbackFailures) == 1) Error("Capture callback", error);
    }

    public string Snapshot()
    {
        lock (gate)
            return $"Frames received: {Interlocked.Read(ref received)}; rejected: {Interlocked.Read(ref rejected)}; superseded: {Interlocked.Read(ref superseded)}; callback failures: {Interlocked.Read(ref callbackFailures)}{Environment.NewLine}"
                + $"Rendering: {rendered} frames; mean {(rendered == 0 ? 0 : renderingMilliseconds / rendered):F2} ms; maximum {maximumRenderMilliseconds:F2} ms{Environment.NewLine}"
                + string.Join(Environment.NewLine, events);
    }
}
