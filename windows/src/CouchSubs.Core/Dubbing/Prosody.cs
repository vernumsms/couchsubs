// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core.Dubbing;

/// <summary>
/// Konuşmanın söyleniş biçimi: ses yüksekliği (dB), perdesi (Hz, ortanca) ve perdenin ne kadar oynadığı (yarım ton).
/// Bağıran, heyecanlı konuşma yüksek ve oynak; sakin, yorgun konuşma alçak ve düz olur.
/// </summary>
public readonly record struct Prosody(double Loudness, double Pitch, double Spread, double Voiced)
{
    private const int Rate = 16000;
    private const int Frame = 640;  // 40 ms
    private const int Hop = 320;    // 20 ms
    private const int MinLag = Rate / 400;
    private const int MaxLag = Rate / 70;

    /// <summary>Her örnekleme hızındaki ses (doğrusal ara değerle 16 kHz'e indirilir).</summary>
    public static Prosody Measure(SpeechAudio audio)
    {
        if (audio.SampleRate == Rate) return Measure(audio.Samples);
        var step = audio.SampleRate / (double)Rate;
        var length = (int)(audio.Samples.Length / step);
        var resampled = new float[length];
        for (var i = 0; i < length; i++)
        {
            var position = i * step;
            var index = (int)position;
            var next = Math.Min(index + 1, audio.Samples.Length - 1);
            var fraction = (float)(position - index);
            resampled[i] = (audio.Samples[index] * (1 - fraction)) + (audio.Samples[next] * fraction);
        }
        return Measure(resampled);
    }

    /// <summary>İki perde arasındaki fark (yarım ton, mutlak).</summary>
    public static double Semitones(double a, double b) => a > 0 && b > 0 ? Math.Abs(12 * Math.Log2(a / b)) : 0;

    /// <summary>Konuşmanın perdesi bulunabildi mi (müzik ya da gürültü altında bulunamayabilir).</summary>
    public bool HasPitch => Pitch > 0 && Voiced >= 0.15;

    /// <summary>16 kHz tek kanal konuşma. Perde, her 40 ms'lik pencerede öz ilinti (autocorrelation) tepesinden bulunur.</summary>
    public static Prosody Measure(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length < Frame ? 0 : ((samples.Length - Frame) / Hop) + 1;
        if (frames == 0) return default;
        var levels = new double[frames];
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            foreach (var value in samples.Slice(f * Hop, Frame)) sum += value * value;
            levels[f] = Math.Sqrt(sum / Frame);
        }
        var loudest = levels.Max();
        if (loudest < 1e-4) return default;
        var pitches = new List<double>();
        double speechEnergy = 0;
        var speechFrames = 0;
        for (var f = 0; f < frames; f++)
        {
            // Konuşmanın sessiz araları (nefes, hece arası) ölçülmez.
            if (levels[f] < loudest * 0.25) continue;
            speechEnergy += levels[f] * levels[f];
            speechFrames++;
            if (PitchOf(samples.Slice(f * Hop, Frame)) is { } hz) pitches.Add(hz);
        }
        var loudness = 20 * Math.Log10(Math.Sqrt(speechEnergy / Math.Max(1, speechFrames)) + 1e-9);
        if (pitches.Count < 3) return new Prosody(loudness, 0, 0, 0);
        pitches.Sort();
        var median = pitches[pitches.Count / 2];
        // Yarım oktav dışındaki değerler büyük olasılıkla oktav hatası; oynaklığa katılmaz.
        var semitones = pitches.Select(hz => 12 * Math.Log2(hz / median)).Where(s => Math.Abs(s) <= 7).ToList();
        var mean = semitones.Average();
        var spread = Math.Sqrt(semitones.Sum(s => (s - mean) * (s - mean)) / semitones.Count);
        return new Prosody(loudness, median, spread, pitches.Count / (double)Math.Max(1, speechFrames));
    }

    private static double? PitchOf(ReadOnlySpan<float> frame)
    {
        Span<double> scores = stackalloc double[MaxLag + 1];
        var best = 0.0;
        for (var lag = MinLag; lag <= MaxLag; lag++)
        {
            double cross = 0, a = 0, b = 0;
            for (var i = 0; i + lag < frame.Length; i++)
            {
                cross += frame[i] * frame[i + lag];
                a += frame[i] * frame[i];
                b += frame[i + lag] * frame[i + lag];
            }
            scores[lag] = cross / Math.Sqrt((a * b) + 1e-12);
            best = Math.Max(best, scores[lag]);
        }
        if (best < 0.5) return null;
        // Perdenin iki, üç katı gecikmede de tepe olur; tepeye yakın en kısa gecikme asıl perdedir.
        // Eşiği ilk geçen yerden tepenin kendisine tırmanılır, tepe parabolle iki gecikme arasında inceltilir.
        for (var lag = MinLag; lag <= MaxLag; lag++)
        {
            if (scores[lag] < best * 0.92) continue;
            while (lag < MaxLag && scores[lag + 1] > scores[lag]) lag++;
            double offset = 0;
            if (lag > MinLag && lag < MaxLag)
            {
                var curve = scores[lag - 1] - (2 * scores[lag]) + scores[lag + 1];
                if (curve < 0) offset = 0.5 * (scores[lag - 1] - scores[lag + 1]) / curve;
            }
            return Rate / (lag + Math.Clamp(offset, -0.5, 0.5));
        }
        return null;
    }
}

/// <summary>Dublajda kullanılacak ses örneğinin duygusu.</summary>
public enum VoiceMood { Calm, Normal, Intense }
