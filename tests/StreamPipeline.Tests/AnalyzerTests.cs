using System.Diagnostics;
using StreamPipeline.Ingestion;
using StreamPipeline.Processing;

namespace StreamPipeline.Tests;

public class AnalyzerTests
{
    private const int N = 4096;
    private const double Fs = 25600;

    private static SensorFrame Frame(double[] samples, int protocol = SpectralAnalyzer.SupportedProtocol) => new()
    {
        SensorId = 1,
        Sequence = 0,
        ProtocolVersion = protocol,
        SampleRateHz = Fs,
        Samples = samples,
        Length = samples.Length,
        SampledTicks = Stopwatch.GetTimestamp(),
        ReceivedTicks = Stopwatch.GetTimestamp(),
    };

    [Fact]
    public void Fft_PlacesPureToneInCorrectBin()
    {
        var plan = new FftPlan(N);
        var re = new double[N];
        var im = new double[N];
        for (int i = 0; i < N; i++) re[i] = Math.Sin(2 * Math.PI * 37 * i / N);

        plan.Forward(re, im);

        int peak = Enumerable.Range(1, N / 2 - 1).MaxBy(k => re[k] * re[k] + im[k] * im[k]);
        Assert.Equal(37, peak);
        Assert.Equal(N / 2.0, Math.Sqrt(re[37] * re[37] + im[37] * im[37]), 6);
    }

    [Fact]
    public void Fft_InverseRestoresSignal()
    {
        var plan = new FftPlan(1024);
        var rng = new Random(1);
        var original = Enumerable.Range(0, 1024).Select(_ => rng.NextDouble()).ToArray();
        var re = (double[])original.Clone();
        var im = new double[1024];

        plan.Forward(re, im);
        plan.Inverse(re, im);

        for (int i = 0; i < 1024; i++) Assert.Equal(original[i], re[i], 9);
    }

    [Fact]
    public void Analyze_RejectsNonFiniteSamples()
    {
        using var analyzer = new SpectralAnalyzer(N, Fs);
        var samples = new double[N];
        samples[100] = double.NaN;

        Assert.Throws<InvalidDataException>(() => analyzer.Analyze(Frame(samples), Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void Analyze_UnsupportedProtocolIsFatal()
    {
        using var analyzer = new SpectralAnalyzer(N, Fs);
        Assert.Throws<FatalPipelineException>(() =>
            analyzer.Analyze(Frame(new double[N], SpectralAnalyzer.SupportedProtocol + 1), Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void Analyze_DetectsShaftFrequencyAndBearingFault()
    {
        const int n = 16384;
        using var analyzer = new SpectralAnalyzer(n, Fs);
        var gen = new SignalGenerator(1, Fs, seed: 42); // сензор 1 развива дефект
        var rng = new Random(0);

        var healthy = new double[n];
        gen.Fill(healthy, 0, wallSeconds: 0, rng);
        var faulty = new double[n];
        gen.Fill(faulty, 1, wallSeconds: 100, rng);

        var h = analyzer.Analyze(Frame(healthy), Stopwatch.GetTimestamp());
        var f = analyzer.Analyze(Frame(faulty), Stopwatch.GetTimestamp());

        Assert.InRange(h.ShaftFrequencyHz, gen.ShaftHz - 2, gen.ShaftHz + 2);
        Assert.True(f.BearingFaultIndex > 3 * h.BearingFaultIndex,
            $"fault index: healthy {h.BearingFaultIndex:F3}, faulty {f.BearingFaultIndex:F3}");
        Assert.True(f.Kurtosis > h.Kurtosis);
    }
}
