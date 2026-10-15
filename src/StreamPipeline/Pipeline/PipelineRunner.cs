using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using StreamPipeline.Ingestion;
using StreamPipeline.Processing;
using StreamPipeline.Reactive;

namespace StreamPipeline.Pipeline;

/// <summary>
/// Свързва трите модела:
///
///   [N × async сензор] --WriteAsync--> [Bounded Channel] --ReadAllAsync--> [Parallel.ForEachAsync (DOP)] --Publish--> [Rx EventHub] --> абонати
///        асинхронност                     буфер/обратен натиск               паралелизъм                         реактивност
///
/// Прекратяване на два етапа:
///   • плавно (<see cref="RequestStop"/>, изтичане на Duration): спират само източниците,
///     каналът се затваря, работниците изпразват опашката, Rx получава OnCompleted;
///   • твърдо (CancellationToken): всички етапи се прекъсват веднага, остатъкът в опашката се отчита.
/// Грешките се разпространяват „надолу“: грешка при приема → канал, завършен с изключение →
/// паралелният етап → OnError в Rx.
/// </summary>
public sealed class PipelineRunner : IDisposable
{
    private readonly PipelineOptions _o;
    private readonly EventHub _hub = new();
    private readonly CancellationTokenSource _stopCts = new();

    private long _received;
    private long _receivedAfterWarmup;
    private long _dropped;
    private long _rejected;
    private int _offline;
    private long _retries;

    public PipelineRunner(PipelineOptions options) => _o = options;

    /// <summary>Плавно спиране (например при първо Ctrl+C).</summary>
    public void RequestStop() => _stopCts.Cancel();

    public async Task<RunReport> RunAsync(CancellationToken cancellationToken = default)
    {
        ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
        ThreadPool.SetMinThreads(Math.Max(minWorkers, _o.DegreeOfParallelism + 8), minIo);

        using var hardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token, hardCts.Token);
        if (_o.CancelAfter is { } cancelAfter) hardCts.CancelAfter(cancelAfter);

        var channel = CreateChannel();
        long start = Stopwatch.GetTimestamp();
        long warmupEnd = start + (long)(_o.Warmup.TotalSeconds * Stopwatch.Frequency);

        var sensors = Enumerable.Range(0, _o.Sensors)
            .Select(id => new SimulatedSensorClient(id, _o, start))
            .ToArray();

        using var analyzer = new SpectralAnalyzer(_o.FrameSize, _o.SampleRateHz, _o.WelchSegment);
        using var monitor = new ReactiveMonitor(_hub.Events, start, warmupEnd,
            () => new GaugeSnapshot(channel.Reader.Count, Interlocked.Read(ref _received),
                Interlocked.Read(ref _dropped), sensors.Sum(s => s.Overruns)),
            _o.ConsoleDashboard);
        monitor.Start();

        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);

        _stopCts.CancelAfter(_o.Duration);

        // ---- Етап 1: асинхронен прием ----
        Task ingestion = RunIngestionAsync(sensors, channel.Writer, warmupEnd, producerCts.Token, hardCts.Token);

        // ---- Етап 2: паралелна обработка ----
        RunStatus status;
        Exception? fatal = null;
        try
        {
            await RunProcessingAsync(channel.Reader, analyzer, hardCts.Token).ConfigureAwait(false);
            status = hardCts.IsCancellationRequested ? RunStatus.Cancelled : RunStatus.Completed;
        }
        catch (OperationCanceledException) when (hardCts.IsCancellationRequested)
        {
            status = RunStatus.Cancelled;
        }
        catch (Exception ex)
        {
            status = RunStatus.Faulted;
            fatal = ex is AggregateException { InnerExceptions.Count: 1 } ae ? ae.InnerException! : ex;
            hardCts.Cancel(); // спира източниците, блокирани в WriteAsync
        }

        try { await ingestion.ConfigureAwait(false); }
        catch (Exception ex) when (fatal is null && status != RunStatus.Cancelled)
        {
            status = RunStatus.Faulted;
            fatal = ex;
        }
        catch { /* вече отчетено */ }

        long processingEnd = Stopwatch.GetTimestamp();

        // Остатък в опашката при твърдо прекратяване/грешка.
        long left = 0;
        while (channel.Reader.TryRead(out var orphan))
        {
            left++;
            ArrayPool<double>.Shared.Return(orphan.Samples);
        }

        // ---- Етап 3: завършване на реактивния поток ----
        if (fatal is not null) _hub.Fail(fatal);
        else _hub.Complete();

        string reactiveTermination;
        try
        {
            await monitor.Completion.ConfigureAwait(false);
            reactiveTermination = "OnCompleted";
        }
        catch (Exception ex)
        {
            reactiveTermination = $"OnError: {ex.GetType().Name}";
        }

        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuStart;
        double elapsed = Stopwatch.GetElapsedTime(start, processingEnd).TotalSeconds;
        var metrics = monitor.Metrics;

        long measureFrom = Math.Min(warmupEnd, processingEnd);
        long lastCompletion = Math.Max(metrics.LastCompletionTicks, measureFrom + 1);
        double throughputWindow = Stopwatch.GetElapsedTime(measureFrom, lastCompletion).TotalSeconds;
        double offeredWindow = Math.Max(1e-3, Math.Min(_o.Duration.TotalSeconds, elapsed) - _o.Warmup.TotalSeconds);

        return new RunReport
        {
            Options = _o,
            Status = status,
            FatalError = fatal is null ? null : $"{fatal.GetType().Name}: {fatal.Message}",
            ReactiveTermination = reactiveTermination,
            ElapsedSeconds = elapsed,
            MeasuredWindowSeconds = throughputWindow,
            Received = Interlocked.Read(ref _received),
            Processed = metrics.Processed,
            Rejected = Interlocked.Read(ref _rejected),
            Dropped = Interlocked.Read(ref _dropped),
            Overruns = sensors.Sum(s => s.Overruns),
            Retries = Interlocked.Read(ref _retries),
            SensorsOffline = _offline,
            LeftInQueue = left,
            HealthChanges = monitor.HealthChanges.Count,
            OfferedRate = Interlocked.Read(ref _receivedAfterWarmup) / offeredWindow,
            Throughput = throughputWindow > 0 ? metrics.ProcessedAfterWarmup / throughputWindow : 0,
            EndToEndMs = LatencyStats.From(metrics.EndToEndMs),
            QueueWaitMs = LatencyStats.From(metrics.QueueWaitMs),
            ProcessingMs = LatencyStats.From(metrics.ProcessingMs),
            AgeMs = LatencyStats.From(metrics.AgeMs),
            MaxQueueDepth = monitor.MaxQueueDepth,
            CpuCoresUsed = elapsed > 0 ? cpu.TotalSeconds / elapsed : 0,
            Gen0Collections = GC.CollectionCount(0) - gen0,
            Gen2Collections = GC.CollectionCount(2) - gen2,
            DistinctWorkerThreads = metrics.FramesPerThread.Count,
            Artifacts = new RunArtifacts(monitor.Timeline.ToList(), monitor.HealthChanges.ToList(),
                monitor.Errors.ToList(), metrics.Samples, new Dictionary<int, int>(metrics.FramesPerThread)),
        };
    }

    private Channel<SensorFrame> CreateChannel()
    {
        var options = new BoundedChannelOptions(_o.ChannelCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = _o.FullPolicy == QueueFullPolicy.DropOldest
                ? BoundedChannelFullMode.DropOldest
                : BoundedChannelFullMode.Wait,
        };
        return Channel.CreateBounded<SensorFrame>(options, dropped =>
        {
            Interlocked.Increment(ref _dropped);
            ArrayPool<double>.Shared.Return(dropped.Samples);
        });
    }

    // ================= Етап 1: асинхронност (вход/изход) =================

    private async Task RunIngestionAsync(SimulatedSensorClient[] sensors, ChannelWriter<SensorFrame> writer,
        long warmupEnd, CancellationToken ct, CancellationToken hardCt)
    {
        Exception? error = null;
        try
        {
            await Task.WhenAll(sensors.Select(s => RunSensorAsync(s, writer, warmupEnd, ct, hardCt))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // нормално спиране на източниците
        }
        catch (Exception ex)
        {
            error = ex;
            throw;
        }
        finally
        {
            // Затварянето на канала е сигналът за „край на входа“ към паралелния етап.
            writer.TryComplete(error);
        }
    }

    /// <param name="ct">Спира приема на нови кадри (плавно или твърдо спиране).</param>
    /// <param name="hardCt">Само твърдото прекратяване прекъсва записа на вече приет кадър.</param>
    private async Task RunSensorAsync(SimulatedSensorClient sensor, ChannelWriter<SensorFrame> writer,
        long warmupEnd, CancellationToken ct, CancellationToken hardCt)
    {
        while (!ct.IsCancellationRequested)
        {
            SensorFrame frame;
            int attempt = 0;
            while (true)
            {
                try
                {
                    frame = await sensor.ReceiveAsync(ct).ConfigureAwait(false);
                    break;
                }
                catch (IOException ex)
                {
                    attempt++;
                    if (attempt > _o.MaxRetries)
                    {
                        Interlocked.Increment(ref _offline);
                        _hub.Publish(new SensorOffline(sensor.SensorId,
                            $"{ex.Message} (изчерпани {_o.MaxRetries} повторни опита)", Stopwatch.GetTimestamp()));
                        return; // останалите сензори продължават
                    }
                    Interlocked.Increment(ref _retries);
                    _hub.Publish(new SensorRetry(sensor.SensorId, attempt, ex.Message, Stopwatch.GetTimestamp()));
                    // Експоненциално отлагане — изчакване на мрежата, не изчисление.
                    await Task.Delay(_o.RetryBaseDelay * Math.Pow(2, attempt - 1), ct).ConfigureAwait(false);
                }
            }

            Interlocked.Increment(ref _received);
            if (frame.ReceivedTicks >= warmupEnd) Interlocked.Increment(ref _receivedAfterWarmup);

            try
            {
                // Точка на обратен натиск: при пълна опашка (Wait) източникът чака.
                await writer.WriteAsync(frame, hardCt).ConfigureAwait(false);
            }
            catch
            {
                ArrayPool<double>.Shared.Return(frame.Samples);
                throw;
            }
        }
    }

    // ================= Етап 2: паралелизъм (изчисления) =================

    private Task RunProcessingAsync(ChannelReader<SensorFrame> reader, SpectralAnalyzer analyzer, CancellationToken ct)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _o.DegreeOfParallelism,
            CancellationToken = ct,
        };

        return Parallel.ForEachAsync(reader.ReadAllAsync(), options, (frame, token) =>
        {
            long dequeued = Stopwatch.GetTimestamp();
            try
            {
                // Синхронно, CPU-интензивно тяло — върви на нишка от пула.
                var result = analyzer.Analyze(frame, dequeued);
                _hub.Publish(new FrameAnalyzed(result));
            }
            catch (InvalidDataException ex)
            {
                // Локална грешка: кадърът се отхвърля, потокът продължава.
                Interlocked.Increment(ref _rejected);
                _hub.Publish(new FrameRejected(frame.SensorId, frame.Sequence, ex.Message, Stopwatch.GetTimestamp()));
            }
            finally
            {
                ArrayPool<double>.Shared.Return(frame.Samples);
            }
            // FatalPipelineException не се хваща → Parallel.ForEachAsync отменя останалите работници.
            return ValueTask.CompletedTask;
        });
    }

    public void Dispose()
    {
        _hub.Dispose();
        _stopCts.Dispose();
    }
}
