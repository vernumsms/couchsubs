// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

/// <summary>Konuşmayı yazıya çeviren model (oyun sesini dinlemek için; ör. Whisper).</summary>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>16 kHz tek kanal örneklerdeki İngilizce konuşma; konuşma yoksa ya da anlaşılmadıysa metni null.</summary>
    Task<HeardSpeech> TranscribeAsync(float[] samples, CancellationToken cancellationToken);
}

/// <summary>Duyulan konuşma: metin (yoksa null) ve modelin ne kadar emin olduğu (0–1).</summary>
public sealed record HeardSpeech(string? Text, float Confidence);

/// <summary>İndirilmesi gereken konuşma tanıma modeli.</summary>
public interface IGameSpeechModel
{
    bool IsInstalled { get; }
    /// <summary>İndirilecek toplam boyut (bayt).</summary>
    long DownloadBytes { get; }
    Task DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken);
    ISpeechRecognizer CreateRecognizer();
}

/// <summary>Bir konuşma parçası: 16 kHz örnekler ve konuşmanın başladığı an (Environment.TickCount64).</summary>
public sealed record SpeechSegment(float[] Samples, long StartedAt);

/// <summary>
/// Oyun sesini konuşma parçalarına böler: sesi 16 kHz'e indirir, enerjiye bakarak konuşmanın başını ve sonunu bulur.
/// Sessizlik (yarım saniye) gelince ya da parça çok uzayınca (sürekli müzik, uzun konuşma) parçayı verir.
/// Oyun müziği ve patlamalar da "ses" sayılabilir; ayıklamayı konuşma tanıma modeli yapar.
/// </summary>
public sealed class SpeechSegmenter
{
    public const int Rate = 16000;
    /// <summary>Enerjiye bakılan pencere: 30 ms.</summary>
    private const int Window = Rate * 30 / 1000;
    /// <summary>Konuşma bu kadar sessizlikle biter.</summary>
    private const int SilenceWindows = 17;
    /// <summary>Bundan kısa ses (öksürük, tek patlama) konuşma sayılmaz: 0,5 sn.</summary>
    private const int MinSamples = Rate / 2;
    /// <summary>Parça en çok 8 sn olur (gecikme birikmesin).</summary>
    private const int MaxSamples = Rate * 8;
    /// <summary>Konuşmanın başı kesilmesin diye öncesinden eklenen ses: 0,3 sn.</summary>
    private const int PreRollWindows = 10;

    private readonly Action<SpeechSegment> ready;
    private readonly Func<long> clock;
    private readonly List<float> window = new(Window);
    private readonly Queue<float[]> preRoll = new();
    private readonly List<float> speech = [];
    private bool inSpeech;
    private int silent;
    private long startedAt;
    /// <summary>Arka plan ses seviyesi (müzik, ortam); yavaşça izlenir.</summary>
    private float floor = 0.01f;
    /// <summary>Örnekleme hızı dönüştürme: kaynak örneğin 16 kHz'deki karşılığının kesirli konumu.</summary>
    private double position;
    private float accumulator;
    private int accumulated;

    public SpeechSegmenter(Action<SpeechSegment> ready, Func<long>? clock = null)
    {
        this.ready = ready;
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>Tek kanal örnekler (her hızda; 48 kHz beklenir).</summary>
    public void Feed(ReadOnlySpan<float> samples, int rate)
    {
        if (rate <= 0) return;
        // Basit indirgeme: her 16 kHz örneği, karşılık gelen kaynak örneklerin ortalamasıdır (alçak geçiren süzgeç gibi).
        var step = Rate / (double)rate;
        foreach (var sample in samples)
        {
            accumulator += sample;
            accumulated++;
            position += step;
            if (position < 1) continue;
            position -= 1;
            Push(accumulator / accumulated);
            accumulator = 0;
            accumulated = 0;
        }
    }

    private void Push(float sample)
    {
        window.Add(sample);
        if (window.Count < Window) return;
        var chunk = window.ToArray();
        window.Clear();
        double sum = 0;
        foreach (var value in chunk) sum += value * value;
        var level = (float)Math.Sqrt(sum / chunk.Length);
        // Konuşma: arka plan seviyesinin belirgin üstü.
        var loud = level > Math.Max(floor * 2.5f, 0.008f);
        // Arka plan seviyesi sessiz pencerelerde hızlı, yüksek seslerde çok yavaş yükselir.
        floor = loud ? floor + ((level - floor) * 0.002f) : floor + ((level - floor) * 0.05f);

        if (!inSpeech)
        {
            preRoll.Enqueue(chunk);
            while (preRoll.Count > PreRollWindows) preRoll.Dequeue();
            if (!loud) return;
            inSpeech = true;
            silent = 0;
            startedAt = clock() - (preRoll.Count * 30);
            foreach (var earlier in preRoll) speech.AddRange(earlier);
            preRoll.Clear();
            return;
        }
        speech.AddRange(chunk);
        silent = loud ? 0 : silent + 1;
        if (silent >= SilenceWindows || speech.Count >= MaxSamples) Finish();
    }

    /// <summary>Süren konuşmayı hemen verir (ör. oturum kapanırken).</summary>
    public void Finish()
    {
        if (inSpeech && speech.Count - (silent * Window) >= MinSamples) ready(new SpeechSegment([.. speech], startedAt));
        speech.Clear();
        inSpeech = false;
        silent = 0;
    }

    /// <summary>
    /// Konuşma tanımanın sessiz ya da müzikli seste uydurduğu kalıp sözler ("Thank you.", "[Music]") ve yalnız
    /// köşeli/normal parantezli ses açıklamaları atılır; kalan anlamlı metin döner.
    /// </summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|♪+", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Count(char.IsLetter) < 3) return null;
        var plain = new string([.. cleaned.ToLowerInvariant().Where(c => char.IsLetter(c) || c == ' ')]).Trim();
        string[] hallucinations =
        [
            "thank you", "thanks for watching", "thank you for watching", "you", "bye", "okay", "oh", "uh", "um",
            "please subscribe", "subscribe", "see you next time", "so", "hmm",
        ];
        if (hallucinations.Contains(plain)) return null;
        // Konuşma tanıma, anlamadığı sesi (başka dilde konuşma, gürültü) video kapanışı gibi kalıp sözlere çevirir.
        string[] videoPhrases = ["next video", "see you next time", "see you in the next", "thanks for watching", "subscribe"];
        return videoPhrases.Any(plain.Contains) ? null : cleaned;
    }
}
