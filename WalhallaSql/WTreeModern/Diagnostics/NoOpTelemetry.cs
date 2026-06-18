namespace WTreeModern.Diagnostics;

/// <summary>Default-Telemetry, die nichts tut.</summary>
public sealed class NoOpTelemetry : ITelemetry
{
    public static readonly NoOpTelemetry Instance = new();

    public void IncrementCounter(string name, long value = 1) { }
    public void RecordTimer(string name, long microseconds) { }
    public void RecordGauge(string name, long value) { }
}
