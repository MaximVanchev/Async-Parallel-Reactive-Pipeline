namespace StreamPipeline;

public enum QueueFullPolicy
{
    /// <summary>Производителят чака свободно място (обратен натиск към източника).</summary>
    Wait,
    /// <summary>Най-старият кадър в опашката се изхвърля.</summary>
    DropOldest,
}

public enum ArrivalPattern { Uniform, Poisson }

public sealed record PipelineOptions
{
    // --- Вход (асинхронен) ---
    public int Sensors { get; init; } = 16;
    /// <summary>Обща целева честота на входните кадри (кадри/s за всички сензори).</summary>
    public double InputRate { get; init; } = 400;
    public ArrivalPattern Arrival { get; init; } = ArrivalPattern.Uniform;
    public TimeSpan NetworkLatency { get; init; } = TimeSpan.FromMilliseconds(2);
    public TimeSpan NetworkJitter { get; init; } = TimeSpan.FromMilliseconds(1);
    /// <summary>Буфер в устройството: при изоставане над това време сензорът губи кадри (overrun).</summary>
    public TimeSpan DeviceBuffer { get; init; } = TimeSpan.FromSeconds(1);

    // --- Сигнал / изчисления ---
    public int FrameSize { get; init; } = 16384;
    public double SampleRateHz { get; init; } = 25600;
    public int WelchSegment { get; init; } = 1024;

    // --- Координация ---
    public int DegreeOfParallelism { get; init; } = Environment.ProcessorCount;
    public int ChannelCapacity { get; init; } = 256;
    public QueueFullPolicy FullPolicy { get; init; } = QueueFullPolicy.Wait;

    // --- Продължителност ---
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Начален период, изключен от статистиките за латентност/пропускателност.</summary>
    public TimeSpan Warmup { get; init; } = TimeSpan.Zero;

    // --- Грешки и прекъсване ---
    public double TransientFaultProbability { get; init; }
    public double CorruptFrameProbability { get; init; }
    public int MaxRetries { get; init; } = 3;
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(20);
    public int? HardFailSensor { get; init; }
    public TimeSpan HardFailAfter { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>След това време сензор 0 започва да праща кадри с неподдържан протокол → фатална грешка.</summary>
    public TimeSpan? FatalAfter { get; init; }
    /// <summary>Твърдо прекратяване (CancellationToken) след това време.</summary>
    public TimeSpan? CancelAfter { get; init; }

    /// <summary>macOS: работниците искат QoS USER_INTERACTIVE (предпочитат производителните ядра).</summary>
    public bool HighQos { get; init; }

    public int Seed { get; init; } = 42;
    public bool ConsoleDashboard { get; init; } = true;
}
