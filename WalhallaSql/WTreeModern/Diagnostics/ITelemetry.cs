namespace WTreeModern.Diagnostics;

/// <summary>
/// Minimaler Telemetrie-Callback für WTreeModern.
/// Konsumenten implementieren dieses Interface, um Metriken
/// in ihre Monitoring-Infrastruktur zu pumpen (Prometheus, StatsD, etc.).
/// </summary>
public interface ITelemetry
{
    /// <summary>Erhöht einen Counter um 1.</summary>
    void IncrementCounter(string name, long value = 1);

    /// <summary>Misst die Dauer einer Operation (Mikrosekunden).</summary>
    void RecordTimer(string name, long microseconds);

    /// <summary>Setzt einen Gauge-Wert.</summary>
    void RecordGauge(string name, long value);
}
