using System.Diagnostics;
using System.Reactive.Linq;
using StreamPipeline.Pipeline;
using StreamPipeline.Reactive;

namespace StreamPipeline.Tests;

public class PipelineTests
{
    private static PipelineOptions Small => new()
    {
        Sensors = 4,
        InputRate = 200,
        FrameSize = 4096,
        DegreeOfParallelism = 2,
        Duration = TimeSpan.FromSeconds(1.5),
        ConsoleDashboard = false,
    };

    [Fact]
    public async Task GracefulStop_DrainsQueue_AndEveryFrameIsAccountedFor()
    {
        using var runner = new PipelineRunner(Small);
        var report = await runner.RunAsync();

        Assert.Equal(RunStatus.Completed, report.Status);
        Assert.Equal("OnCompleted", report.ReactiveTermination);
        Assert.Equal(0, report.LeftInQueue);
        Assert.True(report.Processed > 200);
        Assert.Equal(report.Received, report.Processed + report.Rejected + report.Dropped);
    }

    [Fact]
    public async Task CorruptFrames_AreRejected_WithoutStoppingTheStream()
    {
        using var runner = new PipelineRunner(Small with { CorruptFrameProbability = 0.2 });
        var report = await runner.RunAsync();

        Assert.Equal(RunStatus.Completed, report.Status);
        Assert.True(report.Rejected > 0);
        Assert.Equal(report.Received, report.Processed + report.Rejected);
    }

    [Fact]
    public async Task SensorThatKeepsFailing_GoesOffline_OthersContinue()
    {
        var o = Small with { HardFailSensor = 0, HardFailAfter = TimeSpan.FromSeconds(0.3), RetryBaseDelay = TimeSpan.FromMilliseconds(5) };
        using var runner = new PipelineRunner(o);
        var report = await runner.RunAsync();

        Assert.Equal(RunStatus.Completed, report.Status);
        Assert.Equal(1, report.SensorsOffline);
        Assert.Equal(o.MaxRetries, report.Retries);
        Assert.Contains(report.Artifacts!.Samples, s => s.SensorId == 2);
    }

    [Fact]
    public async Task FatalError_FaultsPipeline_AndReachesRxAsOnError()
    {
        using var runner = new PipelineRunner(Small with { FatalAfter = TimeSpan.FromSeconds(0.5), Duration = TimeSpan.FromSeconds(10) });
        var sw = Stopwatch.StartNew();
        var report = await runner.RunAsync();

        Assert.Equal(RunStatus.Faulted, report.Status);
        Assert.Contains(nameof(FatalPipelineException), report.FatalError);
        Assert.StartsWith("OnError", report.ReactiveTermination);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "грешката трябва да спре потока преди Duration");
    }

    [Fact]
    public async Task ExternalCancellation_StopsPromptly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(0.5));
        using var runner = new PipelineRunner(Small with { Duration = TimeSpan.FromSeconds(30) });
        var sw = Stopwatch.StartNew();
        var report = await runner.RunAsync(cts.Token);

        Assert.Equal(RunStatus.Cancelled, report.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task DropOldest_UnderOverload_DropsInsteadOfBlocking()
    {
        var o = Small with
        {
            InputRate = 2000,
            FrameSize = 16384,
            DegreeOfParallelism = 1,
            ChannelCapacity = 16,
            FullPolicy = QueueFullPolicy.DropOldest,
        };
        using var runner = new PipelineRunner(o);
        var report = await runner.RunAsync();

        Assert.True(report.Dropped > 0);
        Assert.Equal(0, report.Overruns);
        Assert.Equal(report.Received, report.Processed + report.Dropped + report.Rejected);
    }

    [Fact]
    public void HealthTracker_UsesHysteresis()
    {
        AnalysisResult R(double idx) => new(1, 0, 0, 0, 0, 0, 0, 0, idx, 0, 0, 0, 0, 0);

        var t = HealthTracker.Initial(1);
        t = t.Next(R(0.30));
        Assert.Equal(HealthState.Critical, t.State);

        // Малко под прага — остава Critical благодарение на хистерезиса.
        t = t with { Ewma = HealthTracker.CriticalThreshold - 0.01 };
        t = t.Next(R(HealthTracker.CriticalThreshold - 0.01));
        Assert.Equal(HealthState.Critical, t.State);
    }

    [Fact]
    public void Rx_GroupByScan_EmitsOnlyStateChanges()
    {
        AnalysisResult R(int sensor, double idx) => new(sensor, 0, 0, 0, 0, 0, 0, 0, idx, 0, 0, 0, 0, 0);
        var input = new[] { R(1, 0.02), R(2, 0.02), R(1, 0.5), R(1, 0.5), R(2, 0.02), R(1, 0.5) };

        var changes = input.ToObservable()
            .GroupBy(r => r.SensorId)
            .SelectMany(g => g.Scan(HealthTracker.Initial(g.Key), (s, r) => s.Next(r))
                .DistinctUntilChanged(s => s.State)
                .Where((s, i) => i > 0 || s.State != HealthState.Normal))
            .ToEnumerable()
            .ToList();

        // Сензор 2 е здрав → нищо; сензор 1: EWMA минава през Warning към Critical.
        Assert.All(changes, c => Assert.Equal(1, c.SensorId));
        Assert.Equal([HealthState.Warning, HealthState.Critical], changes.Select(c => c.State));
    }
}
