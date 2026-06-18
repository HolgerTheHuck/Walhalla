namespace WTreeModern.Diagnostics;

/// <summary>Default-Latch-Diagnostics, die nichts tut.</summary>
public sealed class NoOpLatchDiagnostics : ILatchDiagnostics
{
    public static readonly NoOpLatchDiagnostics Instance = new();

    public void OnWaitStart(long handle, bool exclusive) { }
    public void OnWaitEnd(long handle) { }
    public void OnAcquired(long handle, bool exclusive) { }
    public void OnReleased(long handle, bool exclusive) { }
}
