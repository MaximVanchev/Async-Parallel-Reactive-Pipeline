using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using StreamPipeline.Ingestion;
using StreamPipeline.Processing;

namespace StreamPipeline.Pipeline;

public static class ExperimentRunner
{
    /// <summary>
    /// Мрежа от изпълнения: всяка комбинация (политика × входна честота × степен на паралелизъм).
    /// Записва summary.csv (по ред на изпълнение) и суровите данни на всяко изпълнение в runs/.
    /// </summary>
    public static async Task RunGridAsync(PipelineOptions baseOptions, double[] rates, int[] dops,
        QueueFullPolicy[] policies, string outDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        WriteMachineInfo(outDir, baseOptions);

        string summaryPath = Path.Combine(outDir, "summary.csv");
        await File.WriteAllTextAsync(summaryPath, ResultWriter.SummaryHeader + Environment.NewLine, ct);

        int total = rates.Length * dops.Length * policies.Length, i = 0;
        foreach (var policy in policies)
        foreach (var rate in rates)
        foreach (var dop in dops)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            var o = baseOptions with
            {
                InputRate = rate,
                DegreeOfParallelism = dop,
                FullPolicy = policy,
                ConsoleDashboard = false,
            };

            // Изолира изпълненията едно от друго (GC от предишното да не пада в следващото).
            GC.Collect();
            GC.WaitForPendingFinalizers();

            using var runner = new PipelineRunner(o);
            var report = await runner.RunAsync(ct);

            string name = $"{policy.ToString().ToLowerInvariant()}_r{rate.ToString(CultureInfo.InvariantCulture)}_p{dop}";
            ResultWriter.WriteRun(report, Path.Combine(outDir, "runs", name));
            await File.AppendAllTextAsync(summaryPath, ResultWriter.SummaryRow(report) + Environment.NewLine, ct);

            Console.WriteLine(
                $"[{i,3}/{total}] {policy,-10} rate {rate,6} DOP {dop,2} → вход {report.OfferedRate,7:F1}/s, " +
                $"изход {report.Throughput,7:F1}/s, e2e p50 {report.EndToEndMs.P50,8:F2} ms p95 {report.EndToEndMs.P95,8:F2} ms, " +
                $"изхв. {report.Dropped}, overrun {report.Overruns}, CPU {report.CpuCoresUsed:F1} ядра");
        }

        Console.WriteLine($"Готово. Резултати: {Path.GetFullPath(outDir)}");
    }

    /// <summary>Еднонишково измерване на цената на анализа на един кадър (калибриране).</summary>
    public static void Benchmark(PipelineOptions o, int frames)
    {
        using var analyzer = new SpectralAnalyzer(o.FrameSize, o.SampleRateHz, o.WelchSegment);
        var gen = new SignalGenerator(1, o.SampleRateHz, o.Seed);
        var rng = new Random(o.Seed);
        var samples = new double[o.FrameSize];

        SensorFrame Make(long seq, double t)
        {
            gen.Fill(samples, seq, t, rng);
            return new SensorFrame
            {
                SensorId = 1, Sequence = seq, ProtocolVersion = SpectralAnalyzer.SupportedProtocol,
                SampleRateHz = o.SampleRateHz, Samples = samples, Length = o.FrameSize,
                SampledTicks = Stopwatch.GetTimestamp(), ReceivedTicks = Stopwatch.GetTimestamp(),
            };
        }

        for (int i = 0; i < 50; i++) analyzer.Analyze(Make(i, 0), Stopwatch.GetTimestamp()); // JIT/PGO загряване

        var genTimer = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++) gen.Fill(samples, i, 0, rng);
        genTimer.Stop();

        double healthy = 0, faulty = 0;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            bool fault = i % 2 == 1;
            var r = analyzer.Analyze(Make(i, fault ? 100 : 0), Stopwatch.GetTimestamp());
            if (fault) faulty += r.BearingFaultIndex; else healthy += r.BearingFaultIndex;
        }
        sw.Stop();

        double perFrame = sw.Elapsed.TotalMilliseconds / frames;
        Console.WriteLine($"Кадър: {o.FrameSize} отчета @ {o.SampleRateHz} Hz, Welch сегмент {o.WelchSegment}");
        Console.WriteLine($"Анализ (1 нишка):     {perFrame:F3} ms/кадър → {1000 / perFrame:F0} кадъра/s на ядро");
        Console.WriteLine($"Генериране (вход):    {genTimer.Elapsed.TotalMilliseconds / frames:F3} ms/кадър");
        Console.WriteLine($"Индекс дефект: здрав {healthy / (frames / 2.0):F4}, повреден {faulty / (frames / 2.0):F4}");
        Console.WriteLine($"Логически процесори:  {Environment.ProcessorCount}");
    }

    /// <summary>
    /// Контролно измерване: само изчислителният етап (Parallel.For над предварително
    /// генерирани кадри), без канал, сензори и Rx. Показва тавана на хардуера за
    /// дадена степен на паралелизъм — с него се сравнява пропускателността на целия поток.
    /// </summary>
    public static void ParallelBenchmark(PipelineOptions o, int[] dops, double secondsPerDop, string? csvPath)
    {
        using var analyzer = new SpectralAnalyzer(o.FrameSize, o.SampleRateHz, o.WelchSegment);
        var rng = new Random(o.Seed);
        var frames = Enumerable.Range(0, 64).Select(i =>
        {
            var gen = new SignalGenerator(i % 16, o.SampleRateHz, o.Seed);
            var s = new double[o.FrameSize];
            gen.Fill(s, i, 0, rng);
            return new SensorFrame
            {
                SensorId = i % 16, Sequence = i, ProtocolVersion = SpectralAnalyzer.SupportedProtocol,
                SampleRateHz = o.SampleRateHz, Samples = s, Length = o.FrameSize,
                SampledTicks = 0, ReceivedTicks = 0,
            };
        }).ToArray();

        var lines = new List<string> { "dop,frames,seconds,frames_per_s,cpu_cores" };
        Console.WriteLine("DOP | кадъра/s | ускорение | CPU ядра");
        double baseline = 0;
        foreach (int dop in dops)
        {
            Parallel.For(0, dop * 20, new ParallelOptions { MaxDegreeOfParallelism = dop },
                i => analyzer.Analyze(frames[i % frames.Length], 0)); // загряване

            long done = 0;
            var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            long deadline = (long)(secondsPerDop * Stopwatch.Frequency);
            Parallel.For(0, dop, new ParallelOptions { MaxDegreeOfParallelism = dop }, w =>
            {
                int k = w;
                while (sw.ElapsedTicks < deadline)
                {
                    analyzer.Analyze(frames[k++ % frames.Length], 0);
                    Interlocked.Increment(ref done);
                }
            });
            sw.Stop();
            double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds / sw.Elapsed.TotalSeconds;
            double rate = done / sw.Elapsed.TotalSeconds;
            if (baseline == 0) baseline = rate;
            Console.WriteLine($"{dop,3} | {rate,8:F0} | {rate / baseline,9:F2} | {cpu,8:F2}");
            lines.Add(string.Join(',', dop, done, sw.Elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                rate.ToString("0.#", CultureInfo.InvariantCulture), cpu.ToString("0.##", CultureInfo.InvariantCulture)));
        }

        if (csvPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);
            File.WriteAllLines(csvPath, lines);
            Console.WriteLine($"Записано: {Path.GetFullPath(csvPath)}");
        }
    }

    private static void WriteMachineInfo(string dir, PipelineOptions o)
    {
        var info = new
        {
            Date = DateTime.Now.ToString("s"),
            Os = RuntimeInformation.OSDescription,
            Arch = RuntimeInformation.ProcessArchitecture.ToString(),
            Runtime = RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            ServerGc = System.Runtime.GCSettings.IsServerGC,
            BaseOptions = o,
        };
        File.WriteAllText(Path.Combine(dir, "machine.json"), JsonSerializer.Serialize(info, ResultWriter.Json));
    }
}
