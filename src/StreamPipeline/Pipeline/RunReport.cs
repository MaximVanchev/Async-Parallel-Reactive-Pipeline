using System.Text.Json.Serialization;
using StreamPipeline.Reactive;

namespace StreamPipeline.Pipeline;

public enum RunStatus
{
    /// <summary>Плавно спиране: източниците спряха, опашката е изпразнена, потокът завърши с OnCompleted.</summary>
    Completed,
    /// <summary>Твърдо прекратяване чрез CancellationToken.</summary>
    Cancelled,
    /// <summary>Неустранима грешка; потокът завърши с OnError.</summary>
    Faulted,
}

public sealed record RunReport
{
    public required PipelineOptions Options { get; init; }
    public required RunStatus Status { get; init; }
    public string? FatalError { get; init; }
    public string? ReactiveTermination { get; init; }

    public required double ElapsedSeconds { get; init; }
    public required double MeasuredWindowSeconds { get; init; }

    public required long Received { get; init; }
    public required long Processed { get; init; }
    public required long Rejected { get; init; }
    public required long Dropped { get; init; }
    public required long Overruns { get; init; }
    public required long Retries { get; init; }
    public required int SensorsOffline { get; init; }
    public required long LeftInQueue { get; init; }
    public required int HealthChanges { get; init; }

    /// <summary>Действителна входна честота след загряването (кадри/s).</summary>
    public required double OfferedRate { get; init; }
    /// <summary>Пропускателна способност след загряването (кадри/s).</summary>
    public required double Throughput { get; init; }

    public required LatencyStats EndToEndMs { get; init; }
    public required LatencyStats QueueWaitMs { get; init; }
    public required LatencyStats ProcessingMs { get; init; }
    public required LatencyStats AgeMs { get; init; }

    public required int MaxQueueDepth { get; init; }
    public required double CpuCoresUsed { get; init; }
    public required int Gen0Collections { get; init; }
    public required int Gen2Collections { get; init; }
    public required int DistinctWorkerThreads { get; init; }

    /// <summary>Сурови данни от реактивния етап (записват се в CSV, не в JSON).</summary>
    [JsonIgnore]
    public RunArtifacts? Artifacts { get; init; }
}

public sealed record RunArtifacts(
    IReadOnlyList<TimelineRow> Timeline,
    IReadOnlyList<HealthChanged> HealthChanges,
    IReadOnlyList<PipelineEvent> Errors,
    IReadOnlyList<AnalysisResult> Samples,
    IReadOnlyDictionary<int, int> FramesPerThread);
