using System.Diagnostics;

namespace StreamPipeline;

/// <summary>
/// Един кадър от вибрационен сензор: N последователни отчета (акселерометър, g),
/// получени по мрежата. Буферът е нает от ArrayPool и се връща след обработка.
/// </summary>
public sealed class SensorFrame
{
    public required int SensorId { get; init; }
    public required long Sequence { get; init; }
    public required int ProtocolVersion { get; init; }
    public required double SampleRateHz { get; init; }
    public required double[] Samples { get; init; }
    public required int Length { get; init; }

    /// <summary>Момент, в който сензорът е заснел кадъра (по разписание).</summary>
    public required long SampledTicks { get; init; }

    /// <summary>Момент, в който асинхронното четене е завършило (кадърът е в приложението).</summary>
    public required long ReceivedTicks { get; init; }
}

public enum HealthState { Normal, Warning, Critical }

/// <summary>Резултат от паралелния (изчислителен) етап.</summary>
public sealed record AnalysisResult(
    int SensorId,
    long Sequence,
    double Rms,
    double Peak,
    double CrestFactor,
    double Kurtosis,
    double ShaftFrequencyHz,
    double HighFrequencyEnergyRatio,
    double BearingFaultIndex,
    long SampledTicks,
    long ReceivedTicks,
    long DequeuedTicks,
    long CompletedTicks,
    int WorkerThreadId)
{
    public double QueueWaitMs => Ms(ReceivedTicks, DequeuedTicks);
    public double ProcessingMs => Ms(DequeuedTicks, CompletedTicks);
    public double EndToEndMs => Ms(ReceivedTicks, CompletedTicks);
    public double AgeMs => Ms(SampledTicks, CompletedTicks);

    private static double Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
}

// ---- Събития, разпространявани реактивно (System.Reactive) ----

public abstract record PipelineEvent(long Ticks);

public sealed record FrameAnalyzed(AnalysisResult Result) : PipelineEvent(Result.CompletedTicks);

public sealed record FrameRejected(int SensorId, long Sequence, string Reason, long At) : PipelineEvent(At);

public sealed record SensorRetry(int SensorId, int Attempt, string Error, long At) : PipelineEvent(At);

public sealed record SensorOffline(int SensorId, string Reason, long At) : PipelineEvent(At);

public sealed record HealthChanged(int SensorId, HealthState State, double Index, long Sequence, long At) : PipelineEvent(At);

/// <summary>Неустранима грешка — прекратява целия поток.</summary>
public sealed class FatalPipelineException(string message) : Exception(message);
