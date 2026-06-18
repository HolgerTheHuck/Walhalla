namespace WTreeModern.Diagnostics;

/// <summary>Default-Logger, der nichts tut.</summary>
public sealed class NoOpLogger : ILogger
{
    public static readonly NoOpLogger Instance = new();

    public void Log(LogLevel level, string message, Exception? exception = null)
    {
        // No-op
    }
}
