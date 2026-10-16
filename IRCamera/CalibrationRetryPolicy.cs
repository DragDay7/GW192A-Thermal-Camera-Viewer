namespace IRCamera;

/// <summary>Bounded retries driven by a monotonic clock, without sleeping on the UI thread.</summary>
internal sealed class CalibrationRetryPolicy(Func<long>? clock = null)
{
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private long nextAttempt;
    public int Remaining { get; private set; }
    public bool InProgress { get; private set; }

    public void Reset() { Remaining = 5; InProgress = false; nextAttempt = now(); }
    public void Cancel() { Remaining = 0; InProgress = false; }

    public bool TryBegin()
    {
        if (InProgress || Remaining == 0 || now() < nextAttempt) return false;
        InProgress = true;
        Remaining--;
        return true;
    }

    public void Complete(bool success, bool permanentFailure = false)
    {
        InProgress = false;
        if (success || permanentFailure) Remaining = 0;
        nextAttempt = now() + 1000;
    }
}
