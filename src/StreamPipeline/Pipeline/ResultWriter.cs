using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamPipeline.Pipeline;

/// <summary>Записва суровите данни от изпълнение: JSON резюме + CSV файлове.</summary>
public static class ResultWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void WriteRun(RunReport report, string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(report, Json));

        var a = report.Artifacts;
        if (a is null) return;

        var sb = new StringBuilder();
        sb.AppendLine("second,processed,rejected,retries,offline,health_changes,throughput_per_s,e2e_p50_ms,e2e_p95_ms,queue_wait_mean_ms,max_queue_depth,received_total,dropped_total,overruns_total");
        foreach (var r in a.Timeline)
            sb.AppendLine(string.Join(',', F(r.Second), r.Processed, r.Rejected, r.Retries, r.Offline, r.HealthChanges,
                F(r.ThroughputPerSec), F(r.EndToEndP50Ms), F(r.EndToEndP95Ms), F(r.QueueWaitMeanMs), r.MaxQueueDepth,
                r.ReceivedTotal, r.DroppedTotal, r.OverrunsTotal));
        File.WriteAllText(Path.Combine(dir, "timeline.csv"), sb.ToString());

        sb.Clear();
        sb.AppendLine("sensor,sequence,thread,queue_wait_ms,processing_ms,e2e_ms,age_ms,rms,kurtosis,shaft_hz,hf_ratio,fault_index");
        foreach (var s in a.Samples)
            sb.AppendLine(string.Join(',', s.SensorId, s.Sequence, s.WorkerThreadId, F(s.QueueWaitMs), F(s.ProcessingMs),
                F(s.EndToEndMs), F(s.AgeMs), F(s.Rms), F(s.Kurtosis), F(s.ShaftFrequencyHz), F(s.HighFrequencyEnergyRatio),
                F(s.BearingFaultIndex)));
        File.WriteAllText(Path.Combine(dir, "latencies.csv"), sb.ToString());

        sb.Clear();
        sb.AppendLine("type,sensor,detail");
        foreach (var h in a.HealthChanges)
            sb.AppendLine($"health,{h.SensorId},{h.State} index={F(h.Index)} seq={h.Sequence}");
        foreach (var e in a.Errors)
        {
            string line = e switch
            {
                SensorRetry r => $"retry,{r.SensorId},\"attempt {r.Attempt}: {r.Error}\"",
                SensorOffline o => $"offline,{o.SensorId},\"{o.Reason}\"",
                FrameRejected j => $"rejected,{j.SensorId},\"seq {j.Sequence}: {j.Reason}\"",
                _ => $"other,,{e.GetType().Name}",
            };
            sb.AppendLine(line);
        }
        File.WriteAllText(Path.Combine(dir, "events.csv"), sb.ToString());

        sb.Clear();
        sb.AppendLine("thread_id,frames");
        foreach (var (thread, frames) in a.FramesPerThread.OrderBy(kv => kv.Key))
            sb.AppendLine($"{thread},{frames}");
        File.WriteAllText(Path.Combine(dir, "threads.csv"), sb.ToString());
    }

    public const string SummaryHeader =
        "policy,target_rate,dop,sensors,duration_s,status,offered_rate,throughput,received,processed,rejected,dropped,overruns," +
        "e2e_mean_ms,e2e_p50_ms,e2e_p95_ms,e2e_p99_ms,e2e_max_ms,queue_wait_mean_ms,queue_wait_p95_ms," +
        "proc_mean_ms,proc_p95_ms,age_p95_ms,max_queue_depth,cpu_cores,gen0,gen2,worker_threads";

    public static string SummaryRow(RunReport r)
    {
        var o = r.Options;
        return string.Join(',',
            o.FullPolicy, F(o.InputRate), o.DegreeOfParallelism, o.Sensors, F(o.Duration.TotalSeconds), r.Status,
            F(r.OfferedRate), F(r.Throughput), r.Received, r.Processed, r.Rejected, r.Dropped, r.Overruns,
            F(r.EndToEndMs.Mean), F(r.EndToEndMs.P50), F(r.EndToEndMs.P95), F(r.EndToEndMs.P99), F(r.EndToEndMs.Max),
            F(r.QueueWaitMs.Mean), F(r.QueueWaitMs.P95), F(r.ProcessingMs.Mean), F(r.ProcessingMs.P95), F(r.AgeMs.P95),
            r.MaxQueueDepth, F(r.CpuCoresUsed), r.Gen0Collections, r.Gen2Collections, r.DistinctWorkerThreads);
    }

    private static string F(double v) => v.ToString("0.###", Inv);
}
