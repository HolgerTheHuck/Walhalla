namespace WTreeModern.Diagnostics;

/// <summary>
/// Minimales Logging-Interface. Keine externen Abhängigkeiten.
/// Die Library verwendet NoOpLogger als Default – wer nichts übergibt,
/// zahlt 0 Overhead.
/// </summary>
public interface ILogger
{
    void Log(LogLevel level, string message, Exception? exception = null);
}
