using System.Diagnostics;

namespace StreamPipeline.Processing;

/// <summary>
/// Изчислително интензивната част: вибрационна диагностика на един кадър.
///   1. Валидиране + статистики във времевата област (RMS, пик, crest factor, ексцес).
///   2. Спектрална плътност по Welch (Hann прозорец, 50% припокриване) → дял на
///      високочестотната енергия.
///   3. FFT на целия кадър → честота на въртене на вала (доминантна линия).
///   4. Обвиващ анализ (envelope analysis): лентов филтър около резонанса чрез FFT,
///      аналитичен сигнал (Хилбертова трансформация) чрез IFFT, спектър на обвиващата
///      → индекс на дефект на външния пръстен на лагера (BPFO).
/// Класът е безопасен за паралелно извикване: работните буфери са thread-local.
/// </summary>
public sealed class SpectralAnalyzer : IDisposable
{
    public const int SupportedProtocol = 2;

    /// <summary>BPFO = коефициент × честота на вала (геометрия на лагера, типичен 6205).</summary>
    public const double BpfoRatio = 3.57;

    public const double ResonanceBandLowHz = 2000;
    public const double ResonanceBandHighHz = 4500;

    private readonly int _n;
    private readonly int _seg;
    private readonly double _fs;
    private readonly FftPlan _planFrame;
    private readonly FftPlan _planSegment;
    private readonly double[] _hann;
    private readonly ThreadLocal<Workspace> _workspace;

    public SpectralAnalyzer(int frameSize, double sampleRateHz, int welchSegment = 1024)
    {
        _n = frameSize;
        _seg = welchSegment;
        _fs = sampleRateHz;
        _planFrame = new FftPlan(frameSize);
        _planSegment = new FftPlan(welchSegment);
        _hann = new double[welchSegment];
        for (int i = 0; i < welchSegment; i++)
            _hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (welchSegment - 1));
        _workspace = new ThreadLocal<Workspace>(() => new Workspace(frameSize, welchSegment));
    }

    public AnalysisResult Analyze(SensorFrame frame, long dequeuedTicks)
    {
        if (frame.ProtocolVersion != SupportedProtocol)
            throw new FatalPipelineException(
                $"Сензор {frame.SensorId} изпрати кадър с протокол v{frame.ProtocolVersion}; поддържа се само v{SupportedProtocol}. Данните не могат да бъдат интерпретирани.");
        if (frame.Length != _n)
            throw new InvalidDataException($"Очаквани {_n} отчета, получени {frame.Length}.");

        ReadOnlySpan<double> x = frame.Samples.AsSpan(0, _n);
        var ws = _workspace.Value!;

        // ---- 1. Времева област ----
        double sum = 0, peak = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            if (!double.IsFinite(v))
                throw new InvalidDataException($"Невалиден отчет (NaN/∞) на позиция {i}.");
            sum += v;
            double a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        double mean = sum / _n;
        double m2 = 0, m4 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double d = x[i] - mean;
            double d2 = d * d;
            m2 += d2;
            m4 += d2 * d2;
        }
        m2 /= _n;
        m4 /= _n;
        double rms = Math.Sqrt(m2);
        double kurtosis = m2 > 0 ? m4 / (m2 * m2) : 0;
        double crest = rms > 0 ? peak / rms : 0;

        // ---- 2. Welch PSD ----
        double hfRatio = WelchHighFrequencyRatio(x, mean, ws);

        // ---- 3. Спектър на целия кадър → честота на вала ----
        var re = ws.Re;
        var im = ws.Im;
        for (int i = 0; i < _n; i++) { re[i] = x[i] - mean; im[i] = 0; }
        _planFrame.Forward(re, im);

        double binHz = _fs / _n;
        int kLo = Math.Max(1, (int)(5 / binHz));
        int kHi = (int)(200 / binHz);
        int kShaft = kLo;
        double best = 0;
        for (int k = kLo; k <= kHi; k++)
        {
            double p = re[k] * re[k] + im[k] * im[k];
            if (p > best) { best = p; kShaft = k; }
        }
        double shaftHz = kShaft * binHz;

        // ---- 4. Обвиващ анализ ----
        // Аналитичен сигнал само в резонансната лента: положителните честоти ×2, останалото 0.
        int bLo = (int)(ResonanceBandLowHz / binHz);
        int bHi = (int)(ResonanceBandHighHz / binHz);
        for (int k = 0; k < _n; k++)
        {
            if (k >= bLo && k <= bHi) { re[k] *= 2; im[k] *= 2; }
            else { re[k] = 0; im[k] = 0; }
        }
        _planFrame.Inverse(re, im);

        // |z(t)| = обвиваща; премахваме средната и правим втора FFT.
        double envMean = 0;
        for (int i = 0; i < _n; i++)
        {
            double e = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
            re[i] = e;
            envMean += e;
        }
        envMean /= _n;
        for (int i = 0; i < _n; i++) { re[i] -= envMean; im[i] = 0; }
        _planFrame.Forward(re, im);

        double bpfo = BpfoRatio * shaftHz;
        double total = 0, atFault = 0;
        int eLo = Math.Max(1, (int)(2 / binHz));
        int eHi = (int)(1000 / binHz);
        for (int k = eLo; k <= eHi; k++)
            total += re[k] * re[k] + im[k] * im[k];
        for (int h = 1; h <= 3; h++)
        {
            int c = (int)Math.Round(h * bpfo / binHz);
            for (int k = c - 2; k <= c + 2; k++)
                if (k >= eLo && k <= eHi) atFault += re[k] * re[k] + im[k] * im[k];
        }
        double faultIndex = total > 0 ? atFault / total : 0;

        return new AnalysisResult(
            frame.SensorId, frame.Sequence, rms, peak, crest, kurtosis, shaftHz, hfRatio, faultIndex,
            frame.SampledTicks, frame.ReceivedTicks, dequeuedTicks, Stopwatch.GetTimestamp(),
            Environment.CurrentManagedThreadId);
    }

    private double WelchHighFrequencyRatio(ReadOnlySpan<double> x, double mean, Workspace ws)
    {
        var psd = ws.Psd;
        Array.Clear(psd);
        var sre = ws.SegRe;
        var sim = ws.SegIm;
        int hop = _seg / 2;
        for (int start = 0; start + _seg <= _n; start += hop)
        {
            for (int i = 0; i < _seg; i++)
            {
                sre[i] = (x[start + i] - mean) * _hann[i];
                sim[i] = 0;
            }
            _planSegment.Forward(sre, sim);
            for (int k = 0; k <= _seg / 2; k++)
                psd[k] += sre[k] * sre[k] + sim[k] * sim[k];
        }

        double binHz = _fs / _seg;
        double total = 0, hf = 0;
        for (int k = 1; k <= _seg / 2; k++)
        {
            total += psd[k];
            if (k * binHz >= ResonanceBandLowHz) hf += psd[k];
        }
        return total > 0 ? hf / total : 0;
    }

    public void Dispose() => _workspace.Dispose();

    private sealed class Workspace(int n, int seg)
    {
        public readonly double[] Re = new double[n];
        public readonly double[] Im = new double[n];
        public readonly double[] SegRe = new double[seg];
        public readonly double[] SegIm = new double[seg];
        public readonly double[] Psd = new double[seg / 2 + 1];
    }
}
