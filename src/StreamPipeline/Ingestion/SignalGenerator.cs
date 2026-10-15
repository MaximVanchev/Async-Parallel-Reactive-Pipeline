namespace StreamPipeline.Ingestion;

/// <summary>
/// Детерминиран генератор на вибрационен сигнал за един сензор (възпроизводим чрез seed):
///   • въртене на вала f0 и хармоници 2f0, 3f0;
///   • шум;
///   • при развиващ се дефект на лагера — затихващи импулси, възбуждащи резонанс
///     (~3 kHz), повтарящи се с честота BPFO = 3.57·f0.
/// Основната форма се изчислява веднъж (таблица), така че „приемането“ на кадър е
/// евтино и не замърсява измерванията на изчислителния етап.
/// </summary>
public sealed class SignalGenerator
{
    private const int TableLength = 1 << 17;
    private const int NoiseLength = 1 << 16;
    private const int KernelLength = 160;

    private static readonly double[] Noise = BuildNoise();

    private readonly double[] _table = new double[TableLength];
    private readonly double[] _kernel = new double[KernelLength];
    private readonly double _fs;

    public int SensorId { get; }
    public double ShaftHz { get; }
    public double BpfoHz { get; }

    /// <summary>Начало на развитие на дефекта (s от старта); null = здрава машина.</summary>
    public double? FaultOnsetSeconds { get; }

    public SignalGenerator(int sensorId, double sampleRateHz, int seed)
    {
        SensorId = sensorId;
        _fs = sampleRateHz;

        // Цяло число периоди в таблицата → без прекъсване при „навиване“.
        int cycles = 125 + 7 * sensorId;
        ShaftHz = cycles * sampleRateHz / TableLength;
        BpfoHz = Processing.SpectralAnalyzer.BpfoRatio * ShaftHz;

        var rng = new Random(seed * 7919 + sensorId);
        double p2 = rng.NextDouble() * 2 * Math.PI;
        double p3 = rng.NextDouble() * 2 * Math.PI;
        for (int i = 0; i < TableLength; i++)
        {
            double w = 2 * Math.PI * cycles * i / TableLength;
            _table[i] = 1.0 * Math.Sin(w) + 0.45 * Math.Sin(2 * w + p2) + 0.2 * Math.Sin(3 * w + p3);
        }

        double resonance = 3000 + 40 * (sensorId % 8);
        double tau = 0.0006;
        for (int i = 0; i < KernelLength; i++)
        {
            double t = i / sampleRateHz;
            _kernel[i] = Math.Exp(-t / tau) * Math.Sin(2 * Math.PI * resonance * t);
        }

        if (sensorId % 4 == 1)
            FaultOnsetSeconds = 1.5 + (sensorId * 1.37) % 5.0;
    }

    /// <summary>Степен на повреда 0..1 към момент t (s от старта на изпълнението).</summary>
    public double Degradation(double tSeconds)
    {
        if (FaultOnsetSeconds is not { } onset || tSeconds <= onset) return 0;
        return Math.Min(1.0, (tSeconds - onset) / 3.0);
    }

    public void Fill(Span<double> dst, long sequence, double wallSeconds, Random rng)
    {
        int n = dst.Length;
        long startSample = sequence * n;

        int off = (int)(startSample % TableLength);
        for (int i = 0; i < n; i++)
        {
            dst[i] = _table[off];
            if (++off == TableLength) off = 0;
        }

        int noiseOff = rng.Next(NoiseLength);
        const int mask = NoiseLength - 1;
        for (int i = 0; i < n; i++)
            dst[i] += Noise[(noiseOff + i) & mask];

        double d = Degradation(wallSeconds);
        if (d <= 0) return;

        double t0 = startSample / _fs;
        double t1 = (startSample + n) / _fs;
        for (long k = (long)Math.Ceiling(t0 * BpfoHz); k / BpfoHz < t1; k++)
        {
            int pos = (int)((k / BpfoHz - t0) * _fs) + rng.Next(-3, 4); // лек случаен приплъзване (slip)
            double amp = 2.2 * d * (0.8 + 0.4 * rng.NextDouble());
            for (int j = 0; j < KernelLength; j++)
            {
                int p = pos + j;
                if ((uint)p < (uint)n) dst[p] += amp * _kernel[j];
            }
        }
    }

    private static double[] BuildNoise()
    {
        var rng = new Random(12345);
        var noise = new double[NoiseLength];
        for (int i = 0; i < NoiseLength; i += 2)
        {
            // Box–Muller → гаусов шум, σ = 0.35 g
            double u1 = 1.0 - rng.NextDouble();
            double u2 = rng.NextDouble();
            double r = Math.Sqrt(-2.0 * Math.Log(u1)) * 0.35;
            noise[i] = r * Math.Cos(2 * Math.PI * u2);
            noise[i + 1] = r * Math.Sin(2 * Math.PI * u2);
        }
        return noise;
    }
}
