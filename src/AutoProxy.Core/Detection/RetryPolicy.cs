namespace AutoProxy.Core.Detection;

/// <summary>
/// Hysteresis gate that prevents rapid switching between modes.
/// A signal must fail <see cref="FailureThreshold"/> times in a row
/// before the caller is allowed to escalate to a full re-detection.
/// </summary>
public class RetryPolicy
{
    private int _consecutiveFailures;

    public RetryPolicy(int failureThreshold)
    {
        if (failureThreshold < 1) failureThreshold = 1;
        FailureThreshold = failureThreshold;
    }

    public int FailureThreshold { get; }
    public int ConsecutiveFailures => _consecutiveFailures;

    public void RecordSuccess() => _consecutiveFailures = 0;

    /// <summary>Returns true when the failure threshold has been reached.</summary>
    public bool RecordFailure()
    {
        _consecutiveFailures++;
        return _consecutiveFailures >= FailureThreshold;
    }

    public void Reset() => _consecutiveFailures = 0;
}