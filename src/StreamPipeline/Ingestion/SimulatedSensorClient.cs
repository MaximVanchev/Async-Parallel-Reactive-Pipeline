using System.Buffers;
using System.Diagnostics;

namespace StreamPipeline.Ingestion;

/// <summary>
/// Асинхронен клиент към отдалечен вибрационен сензор (симулация на мрежова връзка).
/// <see cref="ReceiveAsync"/> моделира мрежово четене: изчакването до пристигане на
/// следващия кадър (разписание + мрежово закъснение) е I/O закъснение и затова
/// се моделира с Task.Delay — нишка не се блокира.
/// </summary>
public sealed class SimulatedSensorClient
{
    private readonly PipelineOptions _o;
    private readonly SignalGenerator _generator;
    private readonly Random _rng;
    private readonly long _startTicks;
    private readonly double _meanIntervalTicks;
    private readonly long _deviceBufferTicks;

    private double _nextDue;
    private long _lastArrival;
    private long _sequence;

    public int SensorId { get; }
    public long Overruns { get; private set; }
    public long Delivered { get; private set; }
    public SignalGenerator Generator => _generator;

    public SimulatedSensorClient(int sensorId, PipelineOptions options, long startTicks)
    {
        SensorId = sensorId;
        _o = options;
        _startTicks = startTicks;
        _rng = new Random(options.Seed * 31 + sensorId);
        _generator = new SignalGenerator(sensorId, options.SampleRateHz, options.Seed);

        double perSensorRate = options.InputRate / options.Sensors;
        _meanIntervalTicks = Stopwatch.Frequency / perSensorRate;
        _deviceBufferTicks = (long)(options.DeviceBuffer.TotalSeconds * Stopwatch.Frequency);
        _nextDue = startTicks + _rng.NextDouble() * _meanIntervalTicks; // разместване на фазите
        _lastArrival = startTicks;
    }

    public async ValueTask<SensorFrame> ReceiveAsync(CancellationToken ct)
    {
        long now = Stopwatch.GetTimestamp();

        // Ако консуматорът (обратен натиск) ни е забавил повече от буфера на устройството,
        // устройството е презаписало най-старите си кадри → загубени (overrun).
        double lag = now - _nextDue;
        if (lag > _deviceBufferTicks)
        {
            long skipped = (long)((lag - _deviceBufferTicks) / _meanIntervalTicks) + 1;
            _nextDue += skipped * _meanIntervalTicks;
            _sequence += skipped;
            Overruns += skipped;
        }

        // Пресмятаме „в локални“ — при мрежова грешка кадърът се изисква повторно.
        long due = (long)_nextDue;
        double latency = _o.NetworkLatency.TotalSeconds + (_rng.NextDouble() * 2 - 1) * _o.NetworkJitter.TotalSeconds;
        long arrival = Math.Max(_lastArrival, due + (long)(Math.Max(0, latency) * Stopwatch.Frequency));

        TimeSpan wait = Stopwatch.GetElapsedTime(now, arrival);
        if (arrival > now && wait > TimeSpan.Zero)
            await Task.Delay(wait, ct).ConfigureAwait(false); // I/O: изчакване на мрежата

        double elapsed = Stopwatch.GetElapsedTime(_startTicks).TotalSeconds;
        if (_o.HardFailSensor == SensorId && elapsed >= _o.HardFailAfter.TotalSeconds)
            throw new IOException($"Сензор {SensorId}: връзката е отказана (connection refused).");
        if (_o.TransientFaultProbability > 0 && _rng.NextDouble() < _o.TransientFaultProbability)
            throw new IOException($"Сензор {SensorId}: връзката е прекъсната (connection reset).");

        // Успешно четене → фиксираме състоянието.
        _lastArrival = arrival;
        _nextDue += NextInterval();
        long seq = _sequence++;

        double[] buffer = ArrayPool<double>.Shared.Rent(_o.FrameSize);
        _generator.Fill(buffer.AsSpan(0, _o.FrameSize), seq, elapsed, _rng);

        if (_o.CorruptFrameProbability > 0 && _rng.NextDouble() < _o.CorruptFrameProbability)
            buffer[_rng.Next(_o.FrameSize)] = double.NaN; // повреден пакет

        int protocol = _o.FatalAfter is { } fatal && SensorId == 0 && elapsed >= fatal.TotalSeconds
            ? Processing.SpectralAnalyzer.SupportedProtocol + 1
            : Processing.SpectralAnalyzer.SupportedProtocol;

        Delivered++;
        return new SensorFrame
        {
            SensorId = SensorId,
            Sequence = seq,
            ProtocolVersion = protocol,
            SampleRateHz = _o.SampleRateHz,
            Samples = buffer,
            Length = _o.FrameSize,
            SampledTicks = due,
            ReceivedTicks = Stopwatch.GetTimestamp(),
        };
    }

    private double NextInterval() => _o.Arrival switch
    {
        ArrivalPattern.Poisson => -Math.Log(1.0 - _rng.NextDouble()) * _meanIntervalTicks,
        _ => _meanIntervalTicks,
    };
}
