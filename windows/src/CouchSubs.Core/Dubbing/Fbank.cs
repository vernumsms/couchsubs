// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core.Dubbing;

/// <summary>
/// Kaldi uyumlu log-mel filtre bankası (80 bant, 25 ms pencere, 10 ms kayma, 16 kHz). WeSpeaker ses tanıma modeli
/// bu özniteliklerle eğitildi (torchaudio.compliance.kaldi.fbank, dither 0); ardından zaman boyunca ortalama çıkarılır.
/// </summary>
public static class Fbank
{
    public const int Rate = 16000;
    public const int Bins = 80;
    private const int FrameLength = 400;
    private const int FrameShift = 160;
    private const int FftSize = 512;
    private const float PreEmphasis = 0.97f;

    private static readonly float[] Window = MakeWindow();
    private static readonly (int Start, float[] Weights)[] MelBanks = MakeMelBanks();

    /// <summary>
    /// 16 kHz örneklerden [kare sayısı × 80] öznitelik (satır satır). Örnekler -1..1 aralığında verilir; Kaldi gibi
    /// 16 bit tamsayı ölçeğine çevrilir. Ortalama çıkarılmış olarak döner.
    /// </summary>
    public static float[] Compute(ReadOnlySpan<float> samples, out int frames)
    {
        frames = samples.Length < FrameLength ? 0 : 1 + (samples.Length - FrameLength) / FrameShift;
        var features = new float[frames * Bins];
        if (frames == 0) return features;
        var frame = new float[FrameLength];
        var real = new double[FftSize];
        var imaginary = new double[FftSize];
        var power = new double[FftSize / 2 + 1];
        for (var f = 0; f < frames; f++)
        {
            var offset = f * FrameShift;
            double mean = 0;
            for (var i = 0; i < FrameLength; i++)
            {
                frame[i] = samples[offset + i] * 32768f;
                mean += frame[i];
            }
            mean /= FrameLength;
            for (var i = 0; i < FrameLength; i++) frame[i] -= (float)mean;
            for (var i = FrameLength - 1; i > 0; i--) frame[i] -= PreEmphasis * frame[i - 1];
            frame[0] -= PreEmphasis * frame[0];
            Array.Clear(real);
            Array.Clear(imaginary);
            for (var i = 0; i < FrameLength; i++) real[i] = frame[i] * Window[i];
            Fft(real, imaginary);
            for (var k = 0; k < power.Length; k++) power[k] = real[k] * real[k] + imaginary[k] * imaginary[k];
            for (var m = 0; m < Bins; m++)
            {
                var (start, weights) = MelBanks[m];
                double energy = 0;
                for (var j = 0; j < weights.Length; j++) energy += weights[j] * power[start + j];
                features[f * Bins + m] = (float)Math.Log(Math.Max(energy, 1.1920929e-07));
            }
        }
        // Ortalama çıkarma (CMN): kayıt ortamının etkisi azalır.
        for (var m = 0; m < Bins; m++)
        {
            double sum = 0;
            for (var f = 0; f < frames; f++) sum += features[f * Bins + m];
            var average = (float)(sum / frames);
            for (var f = 0; f < frames; f++) features[f * Bins + m] -= average;
        }
        return features;
    }

    /// <summary>Povey penceresi: (0,5 − 0,5·cos)^0,85.</summary>
    private static float[] MakeWindow()
    {
        var window = new float[FrameLength];
        for (var i = 0; i < FrameLength; i++)
            window[i] = (float)Math.Pow(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameLength - 1)), 0.85);
        return window;
    }

    private static double Mel(double hz) => 1127.0 * Math.Log(1 + hz / 700.0);

    /// <summary>Kaldi'nin üçgen mel süzgeçleri: 20 Hz – 8 kHz, FFT kutuları 0..255.</summary>
    private static (int, float[])[] MakeMelBanks()
    {
        const int fftBins = FftSize / 2;
        const double low = 20, high = Rate / 2.0;
        var binWidth = (double)Rate / FftSize;
        double melLow = Mel(low), melHigh = Mel(high);
        var delta = (melHigh - melLow) / (Bins + 1);
        var banks = new (int, float[])[Bins];
        for (var m = 0; m < Bins; m++)
        {
            double left = melLow + m * delta, center = left + delta, right = center + delta;
            var weights = new float[fftBins];
            int first = -1, last = -1;
            for (var k = 0; k < fftBins; k++)
            {
                var mel = Mel(binWidth * k);
                if (mel <= left || mel >= right) continue;
                weights[k] = (float)(mel <= center ? (mel - left) / (center - left) : (right - mel) / (right - center));
                if (first < 0) first = k;
                last = k;
            }
            banks[m] = first < 0 ? (0, []) : (first, weights[first..(last + 1)]);
        }
        return banks;
    }

    /// <summary>Yerinde radix-2 FFT.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (var i = 0; i < n; i += length)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < length / 2; k++)
                {
                    int a = i + k, b = a + length / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}
