using System.Globalization;
using System.Text;
using StreamPipeline;
using StreamPipeline.Pipeline;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var cli = Cli.Parse(args.Skip(1));
string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

// Ctrl+C: първо натискане → плавно спиране, второ → твърдо прекратяване.
using var hardCts = new CancellationTokenSource();
PipelineRunner? active = null;
int presses = 0;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (Interlocked.Increment(ref presses) == 1 && active is not null)
    {
        Console.WriteLine(">>> Плавно спиране (източниците спират, опашката се изпразва). Ctrl+C отново за твърдо прекратяване.");
        active.RequestStop();
    }
    else
    {
        Console.WriteLine(">>> Твърдо прекратяване.");
        hardCts.Cancel();
    }
};

try
{
    switch (args[0])
    {
        case "run":
        case "scenario":
        {
            string scenario = args[0] == "scenario" ? cli.Positional ?? "normal" : "normal";
            var options = cli.Apply(Scenarios.Get(scenario));
            string outDir = cli.Get("out") ?? Path.Combine("results", $"{scenario}-{stamp}");

            Console.WriteLine($"Сценарий: {scenario} | сензори {options.Sensors}, вход {options.InputRate}/s, DOP {options.DegreeOfParallelism}, " +
                              $"опашка {options.ChannelCapacity} ({options.FullPolicy}), {options.Duration.TotalSeconds} s");
            using var runner = new PipelineRunner(options);
            active = runner;
            var report = await runner.RunAsync(hardCts.Token);
            ResultWriter.WriteRun(report, outDir);
            PrintReport(report, outDir);
            return report.Status == RunStatus.Faulted ? 2 : 0;
        }

        case "experiment":
        {
            var baseOptions = cli.Apply(new PipelineOptions
            {
                Duration = TimeSpan.FromSeconds(6),
                Warmup = TimeSpan.FromSeconds(1),
            });
            double[] rates = cli.GetList("rates", double.Parse) ?? [100, 250, 500, 1000, 2000, 3000];
            int[] dops = cli.GetList("dops", int.Parse) ?? [1, 2, 4, 8, 12];
            var policies = cli.GetList("policies", Cli.ParsePolicy) ?? [QueueFullPolicy.Wait];
            string outDir = cli.Get("out") ?? Path.Combine("data", $"experiment-{stamp}");
            await ExperimentRunner.RunGridAsync(baseOptions, rates, dops, policies, outDir, hardCts.Token);
            return 0;
        }

        case "bench":
            ExperimentRunner.Benchmark(cli.Apply(new PipelineOptions()), int.Parse(cli.Get("frames") ?? "400"));
            return 0;

        default:
            Console.Error.WriteLine($"Непозната команда '{args[0]}'.");
            PrintHelp();
            return 1;
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Прекратено.");
    return 3;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Грешка в аргументите: {ex.Message}");
    return 1;
}

static void PrintReport(RunReport r, string outDir)
{
    Console.WriteLine();
    Console.WriteLine("================ ОТЧЕТ ================");
    Console.WriteLine($"Статус:                {r.Status}  (реактивен поток: {r.ReactiveTermination})");
    if (r.FatalError is not null) Console.WriteLine($"Фатална грешка:        {r.FatalError}");
    Console.WriteLine($"Време:                 {r.ElapsedSeconds:F2} s");
    Console.WriteLine($"Приети / обработени:   {r.Received} / {r.Processed}  (отхвърлени {r.Rejected}, изхвърлени {r.Dropped}, overrun {r.Overruns}, останали в опашката {r.LeftInQueue})");
    Console.WriteLine($"Повторни опити:        {r.Retries}, сензори извън линия: {r.SensorsOffline}");
    Console.WriteLine($"Входна честота:        {r.OfferedRate:F1} кадъра/s");
    Console.WriteLine($"Пропускателност:       {r.Throughput:F1} кадъра/s");
    Console.WriteLine($"Латентност e2e (ms):   средна {r.EndToEndMs.Mean:F2}, p50 {r.EndToEndMs.P50:F2}, p95 {r.EndToEndMs.P95:F2}, p99 {r.EndToEndMs.P99:F2}, max {r.EndToEndMs.Max:F2}");
    Console.WriteLine($"  чакане в опашка:     средно {r.QueueWaitMs.Mean:F2}, p95 {r.QueueWaitMs.P95:F2}");
    Console.WriteLine($"  изчисление:          средно {r.ProcessingMs.Mean:F2}, p95 {r.ProcessingMs.P95:F2}");
    Console.WriteLine($"Макс. дълбочина опашка:{r.MaxQueueDepth,5}");
    Console.WriteLine($"CPU:                   {r.CpuCoresUsed:F2} ядра средно, {r.DistinctWorkerThreads} работни нишки, GC gen0 {r.Gen0Collections} gen2 {r.Gen2Collections}");
    Console.WriteLine($"Промени в здравето:    {r.HealthChanges}");
    Console.WriteLine($"Сурови данни:          {Path.GetFullPath(outDir)}");
}

static void PrintHelp()
{
    Console.WriteLine("""
        StreamPipeline — асинхронен прием, паралелна обработка, реактивно представяне

        Команди:
          run                        обикновено изпълнение (= scenario normal)
          scenario <име>             normal | faults | fatal | cancel | overload
          experiment                 мрежа от изпълнения за измерване (вж. --rates, --dops, --policies)
          bench                      цена на анализа на един кадър в една нишка

        Опции:
          --rate <кадри/s>           обща входна честота              (400)
          --dop <n>                  степен на паралелизъм            (брой ядра)
          --duration <s>             продължителност                   (10)
          --warmup <s>               изключено от статистиките         (0)
          --sensors <n>              брой сензори                      (16)
          --capacity <n>             капацитет на опашката             (256)
          --policy wait|drop         поведение при пълна опашка        (wait)
          --arrival uniform|poisson  разпределение на пристиганията    (uniform)
          --frame <n>                отчета в кадър, степен на 2       (16384)
          --fault-rate <p>           вероятност за мрежова грешка
          --corrupt-rate <p>         вероятност за повреден кадър
          --hard-fail-sensor <id>    сензор, който отказва трайно
          --fatal-after <s>          неподдържан протокол → фатална грешка
          --cancel-after <s>         твърдо прекратяване
          --seed <n>                 зародиш на генератора              (42)
          --out <папка>              къде да се запишат суровите данни
          --quiet                    без табло в конзолата
          --rates a,b,c  --dops a,b,c  --policies wait,drop   (за experiment)
          --frames <n>               (за bench)

        Ctrl+C веднъж — плавно спиране; два пъти — твърдо прекратяване.
        """);
}

static class Scenarios
{
    public static PipelineOptions Get(string name) => name switch
    {
        "normal" => new PipelineOptions { InputRate = 400, DegreeOfParallelism = 8 },
        "faults" => new PipelineOptions
        {
            InputRate = 400,
            DegreeOfParallelism = 8,
            TransientFaultProbability = 0.02,
            CorruptFrameProbability = 0.01,
            HardFailSensor = 3,
            HardFailAfter = TimeSpan.FromSeconds(3),
        },
        "fatal" => new PipelineOptions
        {
            InputRate = 400,
            DegreeOfParallelism = 8,
            FatalAfter = TimeSpan.FromSeconds(4),
        },
        "cancel" => new PipelineOptions
        {
            InputRate = 3000,
            DegreeOfParallelism = 2,
            CancelAfter = TimeSpan.FromSeconds(4),
        },
        "overload" => new PipelineOptions
        {
            InputRate = 3000,
            DegreeOfParallelism = 2,
            FullPolicy = QueueFullPolicy.DropOldest,
        },
        _ => throw new ArgumentException($"Непознат сценарий '{name}'."),
    };
}

sealed class Cli
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    public string? Positional { get; private set; }

    public static Cli Parse(IEnumerable<string> args)
    {
        var cli = new Cli();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            if (a.StartsWith("--"))
            {
                string key = a[2..];
                string? value = i + 1 < list.Count && !list[i + 1].StartsWith("--") ? list[++i] : null;
                cli._values[key] = value;
            }
            else cli.Positional ??= a;
        }
        return cli;
    }

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v ?? "true" : null;

    public T[]? GetList<T>(string key, Func<string, T> parse) =>
        Get(key)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(parse).ToArray();

    public static QueueFullPolicy ParsePolicy(string s) => s.ToLowerInvariant() switch
    {
        "wait" => QueueFullPolicy.Wait,
        "drop" or "dropoldest" => QueueFullPolicy.DropOldest,
        _ => throw new ArgumentException($"Непозната политика '{s}'."),
    };

    public PipelineOptions Apply(PipelineOptions o)
    {
        TimeSpan Sec(string v) => TimeSpan.FromSeconds(double.Parse(v, CultureInfo.InvariantCulture));
        double D(string v) => double.Parse(v, CultureInfo.InvariantCulture);

        if (Get("rate") is { } rate) o = o with { InputRate = D(rate) };
        if (Get("dop") is { } dop) o = o with { DegreeOfParallelism = int.Parse(dop) };
        if (Get("duration") is { } dur) o = o with { Duration = Sec(dur) };
        if (Get("warmup") is { } wu) o = o with { Warmup = Sec(wu) };
        if (Get("sensors") is { } sensors) o = o with { Sensors = int.Parse(sensors) };
        if (Get("capacity") is { } cap) o = o with { ChannelCapacity = int.Parse(cap) };
        if (Get("policy") is { } pol) o = o with { FullPolicy = ParsePolicy(pol) };
        if (Get("arrival") is { } arr) o = o with { Arrival = arr == "poisson" ? ArrivalPattern.Poisson : ArrivalPattern.Uniform };
        if (Get("frame") is { } frame) o = o with { FrameSize = int.Parse(frame) };
        if (Get("fault-rate") is { } fr) o = o with { TransientFaultProbability = D(fr) };
        if (Get("corrupt-rate") is { } cr) o = o with { CorruptFrameProbability = D(cr) };
        if (Get("hard-fail-sensor") is { } hf) o = o with { HardFailSensor = int.Parse(hf) };
        if (Get("fatal-after") is { } fa) o = o with { FatalAfter = Sec(fa) };
        if (Get("cancel-after") is { } ca) o = o with { CancelAfter = Sec(ca) };
        if (Get("seed") is { } seed) o = o with { Seed = int.Parse(seed) };
        if (Get("quiet") is not null) o = o with { ConsoleDashboard = false };

        if (o.DegreeOfParallelism < 1) throw new ArgumentException("--dop трябва да е ≥ 1.");
        if (o.InputRate <= 0) throw new ArgumentException("--rate трябва да е > 0.");
        if (o.Sensors < 1) throw new ArgumentException("--sensors трябва да е ≥ 1.");
        return o;
    }
}
