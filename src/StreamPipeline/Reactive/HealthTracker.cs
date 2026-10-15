namespace StreamPipeline.Reactive;

/// <summary>
/// Неизменяемо състояние за оператора Scan: експоненциално изгладен (EWMA) индекс на
/// дефект на лагера + машина на състоянията с хистерезис (без „трептене“ на алармите).
/// </summary>
public sealed record HealthTracker(int SensorId, double Ewma, HealthState State, long Sequence, int Count)
{
    public const double Alpha = 0.25;
    public const double WarningThreshold = 0.08;
    public const double CriticalThreshold = 0.17;
    private const double Hysteresis = 0.8;

    public static HealthTracker Initial(int sensorId) => new(sensorId, 0, HealthState.Normal, -1, 0);

    public HealthTracker Next(AnalysisResult r)
    {
        double e = Count == 0 ? r.BearingFaultIndex : Ewma + Alpha * (r.BearingFaultIndex - Ewma);

        HealthState s = State switch
        {
            HealthState.Normal when e >= CriticalThreshold => HealthState.Critical,
            HealthState.Normal when e >= WarningThreshold => HealthState.Warning,
            HealthState.Warning when e >= CriticalThreshold => HealthState.Critical,
            HealthState.Warning when e < WarningThreshold * Hysteresis => HealthState.Normal,
            HealthState.Critical when e < CriticalThreshold * Hysteresis => HealthState.Warning,
            _ => State,
        };

        return new HealthTracker(SensorId, e, s, r.Sequence, Count + 1);
    }
}
