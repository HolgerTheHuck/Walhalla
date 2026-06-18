namespace WTreeModern.Diagnostics;

/// <summary>
/// Wird geworfen, wenn ein Latch-Timeout eintritt oder ein Deadlock-Zyklus
/// im Wait-for-Graph erkannt wird.
/// </summary>
public sealed class WTreeDeadlockException : Exception
{
    public long Handle { get; }
    public bool WasExclusive { get; }
    public TimeSpan Timeout { get; }

    public WTreeDeadlockException(long handle, bool exclusive, TimeSpan timeout)
        : base($"Latch timeout on handle {handle} ({(exclusive ? "exclusive" : "shared")}) after {timeout.TotalMilliseconds} ms. Possible deadlock.")
    {
        Handle = handle;
        WasExclusive = exclusive;
        Timeout = timeout;
    }

    public WTreeDeadlockException(string message)
        : base(message)
    {
        Handle = -1;
    }
}
