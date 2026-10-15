namespace StreamPipeline.Reactive;

/// <summary>
/// Абонат, който натрупва метрики. Извиква се сериализирано (през EventHub),
/// затова не са нужни заключвания.
/// </summary>
public sealed class MetricsRecorder(long warmupEndTicks)
{
    public List<double> EndToEndMs { get; } = new();
    public List<double> QueueWaitMs { get; } = new();
    public List<double> ProcessingMs { get; } = new();
    public List<double> AgeMs { get; } = new();
    public List<AnalysisResult> Samples { get; } = new();
    public Dictionary<int, int> FramesPerThread { get; } = new();

    public long Processed { get; private set; }
    public long ProcessedAfterWarmup { get; private set; }
    public long LastCompletionTicks { get; private set; }
    public long WarmupEndTicks => warmupEndTicks;

    public void Record(AnalysisResult r)
    {
        Processed++;
        if (r.CompletedTicks > LastCompletionTicks) LastCompletionTicks = r.CompletedTicks;
        FramesPerThread[r.WorkerThreadId] = FramesPerThread.GetValueOrDefault(r.WorkerThreadId) + 1;

        if (r.CompletedTicks >= warmupEndTicks) ProcessedAfterWarmup++;
        if (r.ReceivedTicks < warmupEndTicks) return;

        EndToEndMs.Add(r.EndToEndMs);
        QueueWaitMs.Add(r.QueueWaitMs);
        ProcessingMs.Add(r.ProcessingMs);
        AgeMs.Add(r.AgeMs);
        Samples.Add(r);
    }
}

public sealed record LatencyStats(int Count, double Mean, double P50, double P95, double P99, double Max)
{
    public static LatencyStats From(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new LatencyStats(0, 0, 0, 0, 0, 0);
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return new LatencyStats(sorted.Length, sorted.Average(),
            Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99), sorted[^1]);
    }

    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        double rank = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
