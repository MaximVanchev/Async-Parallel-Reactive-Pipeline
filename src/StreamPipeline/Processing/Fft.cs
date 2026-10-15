namespace StreamPipeline.Processing;

/// <summary>
/// Итеративна радикс-2 FFT (Cooley–Tukey) с предварително изчислени
/// тригонометрични коефициенти. Планът е неизменяем и безопасен за
/// използване от много нишки едновременно.
/// </summary>
public sealed class FftPlan
{
    private readonly int[] _bitReverse;
    private readonly double[] _cos;
    private readonly double[] _sin;

    public int Size { get; }

    public FftPlan(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentException("Размерът на FFT трябва да е степен на 2.", nameof(size));

        Size = size;
        int bits = System.Numerics.BitOperations.Log2((uint)size);
        _bitReverse = new int[size];
        for (int i = 0; i < size; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                r |= ((i >> b) & 1) << (bits - 1 - b);
            _bitReverse[i] = r;
        }

        _cos = new double[size / 2];
        _sin = new double[size / 2];
        for (int k = 0; k < size / 2; k++)
        {
            double a = 2 * Math.PI * k / size;
            _cos[k] = Math.Cos(a);
            _sin[k] = Math.Sin(a);
        }
    }

    /// <summary>Права трансформация на място: X[k] = Σ x[n]·e^(−2πikn/N).</summary>
    public void Forward(Span<double> re, Span<double> im)
    {
        int n = Size;
        if (re.Length < n || im.Length < n)
            throw new ArgumentException("Буферите са по-малки от размера на плана.");

        for (int i = 0; i < n; i++)
        {
            int j = _bitReverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            int half = len >> 1;
            int step = n / len;
            for (int start = 0; start < n; start += len)
            {
                for (int j = 0, t = 0; j < half; j++, t += step)
                {
                    double wr = _cos[t];
                    double wi = -_sin[t];
                    int a = start + j;
                    int b = a + half;
                    double tr = re[b] * wr - im[b] * wi;
                    double ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
            }
        }
    }

    /// <summary>Обратна трансформация (със скалиране 1/N) чрез спрегнатия трик.</summary>
    public void Inverse(Span<double> re, Span<double> im)
    {
        int n = Size;
        for (int i = 0; i < n; i++) im[i] = -im[i];
        Forward(re, im);
        double scale = 1.0 / n;
        for (int i = 0; i < n; i++)
        {
            re[i] *= scale;
            im[i] = -im[i] * scale;
        }
    }
}
