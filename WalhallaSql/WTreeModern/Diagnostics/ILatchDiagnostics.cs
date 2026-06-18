namespace WTreeModern.Diagnostics;

/// <summary>
/// Optionaler Callback für Latch-Ereignisse. Ermöglicht Wait-for-Graph-
/// Diagnostik und Deadlock-Erkennung in Debug-/Test-Szenarien.
/// </summary>
public interface ILatchDiagnostics
{
    /// <summary>Wird aufgerufen, bevor ein Thread auf einen Latch wartet.</summary>
    void OnWaitStart(long handle, bool exclusive);

    /// <summary>Wird aufgerufen, wenn der Wait beendet ist (erfolgreich oder abgebrochen).</summary>
    void OnWaitEnd(long handle);

    /// <summary>Wird aufgerufen, wenn der Latch erfolgreich acquired wurde.</summary>
    void OnAcquired(long handle, bool exclusive);

    /// <summary>Wird aufgerufen, wenn der Latch released wurde.</summary>
    void OnReleased(long handle, bool exclusive);
}
