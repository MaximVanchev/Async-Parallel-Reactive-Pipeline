using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace StreamPipeline.Reactive;

public sealed record GaugeSnapshot(int QueueDepth, long Received, long Dropped, long Overruns);

public sealed record TimelineRow(
    double Second,
    int Processed,
    int Rejected,
    int Retries,
    int Offline,
    int HealthChanges,
    double ThroughputPerSec,
    double EndToEndP50Ms,
    double EndToEndP95Ms,
    double QueueWaitMeanMs,
    int MaxQueueDepth,
    long ReceivedTotal,
    long DroppedTotal,
    long OverrunsTotal);

/// <summary>
/// Реактивният етап. Всички абонати делят ЕДИН абонамент към горния поток (Publish/Connect):
///   • метрики           — OfType&lt;FrameAnalyzed&gt; → MetricsRecorder;
///   • аларми            — GroupBy(сензор) → Scan(EWMA + хистерезис) → DistinctUntilChanged;
///   • грешки            — Where(retry/offline/rejected);
///   • времева линия     — Buffer(1 s) → агрегиран ред за секунда (табло);
///   • габарит опашка    — Interval(20 ms) → TakeUntil(край на потока).
/// <see cref="Completion"/> завършва, когато всички абонати са получили OnCompleted/OnError.
/// </summary>
public sealed class ReactiveMonitor : IDisposable
{
    private readonly IObservable<PipelineEvent> _events;
    private readonly long _startTicks;
    private readonly Func<GaugeSnapshot> _gauges;
    private readonly bool _print;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly List<Task> _completions = new();
    private readonly object _listGate = new();

    private int _maxDepthInWindow;
    private int _healthChangesInWindow;
    private long _lastRowTicks;

    public MetricsRecorder Metrics { get; }
    public List<TimelineRow> Timeline { get; } = new();
    public List<HealthChanged> HealthChanges { get; } = new();
    public List<PipelineEvent> Errors { get; } = new();
    public int MaxQueueDepth { get; private set; }

    public Task Completion => Task.WhenAll(_completions);

    public ReactiveMonitor(IObservable<PipelineEvent> events, long startTicks, long warmupEndTicks,
        Func<GaugeSnapshot> gauges, bool printDashboard)
    {
        _events = events;
        _startTicks = startTicks;
        _lastRowTicks = startTicks;
        _gauges = gauges;
        _print = printDashboard;
        Metrics = new MetricsRecorder(warmupEndTicks);
    }

    public void Start()
    {
        var shared = _events.Publish();

        var analyzed = shared.OfType<FrameAnalyzed>().Select(e => e.Result);
        Track(analyzed, Metrics.Record);

        var health = analyzed
            .GroupBy(r => r.SensorId)
            .SelectMany(g => g
                .Scan(HealthTracker.Initial(g.Key), (state, r) => state.Next(r))
                .DistinctUntilChanged(s => s.State)
                .Where((s, i) => i > 0 || s.State != HealthState.Normal)
                .Select(s => new HealthChanged(s.SensorId, s.State, s.Ewma, s.Sequence, Stopwatch.GetTimestamp())));
        Track(health, OnHealthChanged);

        var errors = shared.Where(e => e is SensorRetry or SensorOffline or FrameRejected);
        Track(errors, OnError);

        var timeline = shared.Buffer(TimeSpan.FromSeconds(1)).Select(BuildRow);
        Track(timeline, OnTimelineRow);

        // Край на потока като събитие: Materialize превръща OnCompleted/OnError в OnNext.
        var end = shared.Materialize().Where(n => n.Kind != NotificationKind.OnNext);
        _subscriptions.Add(Observable.Interval(TimeSpan.FromMilliseconds(20))
            .TakeUntil(end)
            .Subscribe(_ => SampleQueueDepth()));

        _subscriptions.Add(shared.Connect());
    }

    private void Track<T>(IObservable<T> source, Action<T> onNext)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _subscriptions.Add(source.Subscribe(onNext, ex => tcs.TrySetException(ex), () => tcs.TrySetResult()));
        _completions.Add(tcs.Task);
    }

    private void SampleQueueDepth()
    {
        int depth = _gauges().QueueDepth;
        int seen;
        do
        {
            seen = Volatile.Read(ref _maxDepthInWindow);
            if (depth <= seen) break;
        } while (Interlocked.CompareExchange(ref _maxDepthInWindow, depth, seen) != seen);
    }

    private void OnHealthChanged(HealthChanged h)
    {
        lock (_listGate) HealthChanges.Add(h);
        Interlocked.Increment(ref _healthChangesInWindow);
        if (_print)
            Console.WriteLine($"  [{Seconds(h.At),6:F2}s] АЛАРМА сензор {h.SensorId,2}: {h.State,-8} (индекс {h.Index:F3}, кадър #{h.Sequence})");
    }

    private void OnError(PipelineEvent e)
    {
        lock (_listGate) Errors.Add(e);
        if (!_print) return;
        if (e is SensorOffline off)
            Console.WriteLine($"  [{Seconds(off.At),6:F2}s] СЕНЗОР {off.SensorId} ИЗВЪН ЛИНИЯ: {off.Reason}");
    }

    private TimelineRow BuildRow(IList<PipelineEvent> batch)
    {
        long now = Stopwatch.GetTimestamp();
        double window = Math.Max(1e-3, Stopwatch.GetElapsedTime(_lastRowTicks, now).TotalSeconds);
        _lastRowTicks = now;

        var e2e = new List<double>(batch.Count);
        double queueSum = 0;
        int rejected = 0, retries = 0, offline = 0;
        foreach (var e in batch)
        {
            switch (e)
            {
                case FrameAnalyzed f: e2e.Add(f.Result.EndToEndMs); queueSum += f.Result.QueueWaitMs; break;
                case FrameRejected: rejected++; break;
                case SensorRetry: retries++; break;
                case SensorOffline: offline++; break;
            }
        }
        var sorted = e2e.ToArray();
        Array.Sort(sorted);
        var g = _gauges();
        int maxDepth = Math.Max(Interlocked.Exchange(ref _maxDepthInWindow, 0), g.QueueDepth);
        MaxQueueDepth = Math.Max(MaxQueueDepth, maxDepth);

        return new TimelineRow(
            Math.Round(Seconds(now), 3), sorted.Length, rejected, retries, offline,
            Interlocked.Exchange(ref _healthChangesInWindow, 0),
            sorted.Length / window,
            LatencyStats.Percentile(sorted, 0.50), LatencyStats.Percentile(sorted, 0.95),
            sorted.Length > 0 ? queueSum / sorted.Length : 0,
            maxDepth, g.Received, g.Dropped, g.Overruns);
    }

    private void OnTimelineRow(TimelineRow r)
    {
        lock (_listGate) Timeline.Add(r);
        if (_print)
            Console.WriteLine(
                $"[{r.Second,6:F2}s] обработени {r.ThroughputPerSec,7:F1}/s | опашка max {r.MaxQueueDepth,4} | " +
                $"e2e p50 {r.EndToEndP50Ms,7:F2} ms p95 {r.EndToEndP95Ms,7:F2} ms | " +
                $"отхв. {r.Rejected} повт. {r.Retries} | изхв. {r.DroppedTotal} overrun {r.OverrunsTotal}");
    }

    private double Seconds(long ticks) => Stopwatch.GetElapsedTime(_startTicks, ticks).TotalSeconds;

    public void Dispose() => _subscriptions.Dispose();
}
