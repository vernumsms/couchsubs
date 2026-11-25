// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using System.Threading.Channels;

namespace CouchSubs.Core.Dubbing;

/// <summary>Konuşanı sesinden tanıyan model (gerçekte WeSpeaker, bkz. CouchSubs.Translation.SpeakerEmbeddingModel).</summary>
public interface ISpeakerModel
{
    bool IsInstalled { get; }
    Task DownloadAsync(CancellationToken cancellationToken);
    ISpeakerEmbedder CreateEmbedder();
}

/// <summary>16 kHz konuşmadan birim uzunlukta ses imzası; konuşma çok kısaysa null.</summary>
public interface ISpeakerEmbedder : IDisposable
{
    float[]? Embed(ReadOnlySpan<float> samples16k);
}

/// <summary>Oyun sesinin son 45 saniyesi (24 kHz, tek kanal); konuşmanın zamanına göre kesit alınır.</summary>
public sealed class GameAudioRing(Func<long>? clock = null)
{
    public const int Rate = 24000;
    private readonly float[] buffer = new float[Rate * 45];
    private readonly Func<long> clock = clock ?? (() => Environment.TickCount64);
    private readonly object gate = new();
    private long total;
    private long lastTick;
    private double position;
    private float accumulator;
    private int accumulated;

    /// <summary>Tek kanal örnekler (48 kHz beklenir); ağ iş parçacığından çağrılır.</summary>
    public void Feed(ReadOnlySpan<float> samples, int rate)
    {
        if (rate <= 0) return;
        var step = Rate / (double)rate;
        lock (gate)
        {
            foreach (var sample in samples)
            {
                accumulator += sample;
                accumulated++;
                position += step;
                if (position < 1) continue;
                // Hızlı kaynakta (48 kHz) iki örneğin ortalaması; yavaş kaynakta aynı örnek birden çok kez yazılır.
                var value = accumulator / accumulated;
                while (position >= 1)
                {
                    position -= 1;
                    buffer[total % buffer.Length] = value;
                    total++;
                }
                accumulator = 0;
                accumulated = 0;
            }
            lastTick = clock();
        }
    }

    /// <summary>[from, to] (Environment.TickCount64) aralığının sesi; tamponda yoksa null.</summary>
    public float[]? Slice(long from, long to)
    {
        lock (gate)
        {
            if (total == 0 || to <= from) return null;
            var end = total - (lastTick - to) * Rate / 1000;
            var start = total - (lastTick - from) * Rate / 1000;
            end = Math.Min(end, total);
            start = Math.Max(start, Math.Max(0, total - buffer.Length));
            if (end - start < Rate / 4) return null;
            var slice = new float[end - start];
            for (var i = 0L; i < slice.Length; i++) slice[i] = buffer[(start + i) % buffer.Length];
            return slice;
        }
    }

    public long Now => clock();
}

/// <summary>
/// Oyun karakterlerinin kendi sesleriyle Türkçe dublaj. Oyun oynanırken her replik, oyun sesinden yazıya çevrilen
/// (Whisper) metin ekrandaki altyazıyla eşleşince kesilir: böylece müzik ya da patlama değil, o anki konuşma alınır.
/// Kesitin ses imzası (WeSpeaker) çıkarılır, aynı sese sahip replikler aynı karakterde toplanır (altyazıda konuşmacı
/// adı varsa ad esas alınır). Her karakterin en temiz kesitleri birleştirilip 5–12 saniyelik bir örnek yapılır;
/// pocket-tts Türkçe cümleyi bu örnekteki sesle okur. Her şey bu bilgisayarda kalır
/// (%LOCALAPPDATA%\CouchSubs\sesler\&lt;oyun&gt;), hiçbir yere gönderilmez; Ayarlar'dan silinebilir.
/// </summary>
public sealed class CharacterVoices : IDisposable
{
    /// <summary>Yeni kesit bu benzerlikten yüksekse var olan karaktere katılır, değilse yeni karakter açılır.</summary>
    public const double JoinThreshold = 0.5;
    /// <summary>Dublaj sırasında konuşan bu benzerlikle tanınırsa onun sesi kullanılır.</summary>
    public const double MatchThreshold = 0.55;
    public const double MinPromptSeconds = 4;
    private const double TargetPromptSeconds = 9;
    private const double MaxPromptSeconds = 12;
    /// <summary>Karakter başına saklanan kesit: sakin, olağan ve coşkulu söyleyişlerin hepsinden örnek kalsın diye çok.</summary>
    private const int MaxClips = 16;
    /// <summary>Satırın coşkusu bundan yüksekse gergin, düşükse sakin örnek kullanılır.</summary>
    public const double MoodThreshold = 0.6;
    private const int MaxCharacters = 200;
    private const long PendingMs = 15_000;

    private readonly string directory;
    private readonly ISpeakerEmbedder embedder;
    private readonly Action<string, long, string>? log;
    private readonly object gate = new();
    private readonly List<Character> characters = [];
    private readonly List<(string Key, long StartedAt, double Seconds, long At)> heard = [];
    private readonly List<(string Key, string? Speaker, long At)> shown = [];
    private readonly Channel<(long StartedAt, double Seconds, string? Speaker, string Text)> work =
        Channel.CreateBounded<(long, double, string?, string)>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, byte[]> promptCache = new(StringComparer.Ordinal);

    public GameAudioRing Audio { get; }

    public CharacterVoices(string directory, ISpeakerEmbedder embedder, Action<string, long, string>? log = null, GameAudioRing? audio = null)
    {
        this.directory = directory;
        this.embedder = embedder;
        this.log = log;
        Audio = audio ?? new GameAudioRing();
        Load();
        worker = Task.Run(WorkAsync);
        listener = Task.Run(ListenAsync);
    }

    private readonly Task worker;
    private readonly Task listener;

    /// <summary>
    /// Oyun sesini zamanlı parçalarla yazıya çeviren model (16 kHz; Whisper). Verilirse her altyazı satırının sesi,
    /// satırın ekranda belirdiği anın çevresinden dinlenip kesilir. Ara sahnelerde konuşma kesintisiz sürdüğü için
    /// ses seviyesine bakarak konuşmayı bölen dinleyici replikleri ayıramıyor, yalnız kırıntılar yakalıyordu.
    /// </summary>
    public Func<float[], CancellationToken, Task<IReadOnlyList<HeardPiece>>>? Transcribe { get; set; }

    /// <summary>Arka plan işleri (kesit işleme ve dinleme) bitince tamamlanır; ses tanıma modeli ancak o zaman bırakılır.</summary>
    public Task Stopped => Task.WhenAll(worker, listener);

    /// <summary>Altyazı satırı belirdikten sonra bu kadar beklenir (konuşmanın sonu da tampona girsin).</summary>
    private const long ListenAfterMs = 3500;
    /// <summary>Satırın konuşması, satır çevrilip gösterildiğinde (okuma + çeviri) başlayalı en çok bu kadar olur.</summary>
    private const long ListenBeforeMs = 6000;
    private readonly List<(string Key, string? Speaker, long At)> waiting = [];

    /// <summary>Kendi sesi hazır (yeterince temiz kesit toplanmış) karakter sayısı.</summary>
    public int ReadyCount { get { lock (gate) return characters.Count(c => c.Ready); } }

    /// <summary>Sesi tanınan karakter sayısı (henüz yeterli kesiti olmayanlar dahil).</summary>
    public int KnownCount { get { lock (gate) return characters.Count; } }

    public event Action? Changed;

    /// <summary>Whisper bir konuşma parçasını yazıya çevirdi.</summary>
    public void Heard(string text, long startedAt, double seconds)
    {
        var key = TextMatch.Normalize(text);
        if (key.Split(' ').Length < 3 || seconds < 1.2) return;
        lock (gate)
        {
            heard.Add((key, startedAt, seconds, Audio.Now));
            MatchLocked();
        }
    }

    /// <summary>Altyazı satırları çevrildi (konuşmacı adıyla, varsa).</summary>
    public void Shown(IReadOnlyList<SpokenLine> lines)
    {
        lock (gate)
        {
            foreach (var line in lines)
            {
                if (line.Background) continue;
                var key = TextMatch.Normalize(line.English);
                if (key.Split(' ').Length < 3) continue;
                shown.Add((key, line.Speaker, Audio.Now));
                if (Transcribe is not null) waiting.Add((key, line.Speaker, Audio.Now));
            }
            if (waiting.Count > 24) waiting.RemoveRange(0, waiting.Count - 24);
            MatchLocked();
        }
    }

    private void MatchLocked()
    {
        var now = Audio.Now;
        heard.RemoveAll(h => now - h.At > PendingMs);
        shown.RemoveAll(s => now - s.At > PendingMs);
        for (var i = heard.Count - 1; i >= 0; i--)
        {
            var h = heard[i];
            var match = shown.FindIndex(s => Overlap(h.Key, s.Key) >= 0.6);
            if (match < 0) continue;
            var line = shown[match];
            heard.RemoveAt(i);
            shown.RemoveAt(match);
            waiting.RemoveAll(w => w.Key == line.Key);
            if (TakeLocked(line.Key)) work.Writer.TryWrite((h.StartedAt, h.Seconds, line.Speaker, line.Key));
        }
    }

    /// <summary>Son bir dakikada sesi alınmış satırlar (aynı replik iki yoldan ya da iki kez alınmasın).</summary>
    private readonly Dictionary<string, long> taken = new(StringComparer.Ordinal);

    private bool TakeLocked(string key)
    {
        var now = Audio.Now;
        foreach (var old in taken.Where(entry => now - entry.Value > 60_000).Select(entry => entry.Key).ToList()) taken.Remove(old);
        return taken.TryAdd(key, now);
    }

    /// <summary>
    /// Bekleyen altyazı satırlarının sesini dinler: en eski satırın çevresindeki ~9,5 sn yazıya çevrilir, Whisper'ın
    /// parçalarından satırla eşleşenin zamanları kesit olur. Pencereye düşen bütün satırlar tek seferde denenir.
    /// </summary>
    private async Task ListenAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(400, stop.Token).ConfigureAwait(false);
                if (Transcribe is not { } transcribe) continue;
                long from, to;
                List<(string Key, string? Speaker, long At)> lines;
                lock (gate)
                {
                    if (waiting.Count == 0 || Audio.Now < waiting[0].At + ListenAfterMs) continue;
                    from = waiting[0].At - ListenBeforeMs;
                    to = waiting[0].At + ListenAfterMs;
                    lines = waiting.Where(w => w.At <= to - 500).ToList();
                    waiting.RemoveAll(w => w.At <= to - 500);
                }
                try { await ListenWindowAsync(transcribe, from, to, lines).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException) { log?.Invoke("ses-toplama-hatasi", 0, error.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ListenWindowAsync(Func<float[], CancellationToken, Task<IReadOnlyList<HeardPiece>>> transcribe,
        long from, long to, List<(string Key, string? Speaker, long At)> lines)
    {
        if (Audio.Slice(from, to) is not { } raw) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Tampon pencerenin başını tutmuyorsa kesit sondan hizalanır.
        var start = to - (long)(raw.Length * 1000.0 / GameAudioRing.Rate);
        var pieces = await transcribe(To16k(raw), stop.Token).ConfigureAwait(false);
        var keys = pieces.Select(p => TextMatch.Normalize(p.Text)).ToArray();
        var matched = 0;
        foreach (var line in lines)
        {
            var best = (Score: 0.0, First: -1, Last: -1);
            for (var first = 0; first < pieces.Count; first++)
                for (var last = first; last < Math.Min(pieces.Count, first + 3); last++)
                {
                    var score = Overlap(string.Join(' ', keys[first..(last + 1)]), line.Key);
                    if (score > best.Score) best = (score, first, last);
                }
            if (best.Score < 0.6) continue;
            lock (gate)
            {
                // Bu arada sesi başka yoldan (duyulan konuşmayla) alındıysa ikinci kez alınmaz.
                if (!TakeLocked(line.Key)) continue;
                shown.RemoveAll(s => s.Key == line.Key);
            }
            var startedAt = start + (long)(pieces[best.First].Start * 1000) - 150;
            var seconds = pieces[best.Last].End - pieces[best.First].Start + 0.3;
            work.Writer.TryWrite((startedAt, seconds, line.Speaker, line.Key));
            matched++;
        }
        log?.Invoke("ses-dinleme", clock.ElapsedMilliseconds,
            $"{matched}/{lines.Count} replik eşleşti | {Shorten(string.Join(" / ", pieces.Select(p => p.Text)))}");
    }

    private static string Shorten(string text) => text.Length <= 240 ? text : text[..240] + "…";

    /// <summary>Ortak sözcük oranı (uzun olana göre): duyulan parça başka bir repliği de içeriyorsa düşük çıkar.</summary>
    public static double Overlap(string a, string b)
    {
        var x = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var y = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (x.Length == 0 || y.Count == 0) return 0;
        var longest = Math.Max(x.Length, y.Count);
        var shared = 0;
        foreach (var word in x)
            if (y.Remove(word)) shared++;
        return (double)shared / longest;
    }

    private async Task WorkAsync()
    {
        try { UpgradeOldVoices(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log?.Invoke("ses-toplama-hatasi", 0, error.Message);
        }
        try
        {
            await foreach (var item in work.Reader.ReadAllAsync(stop.Token))
            {
                try { Collect(item.StartedAt, item.Seconds, item.Speaker, item.Text); }
                catch (Exception error) when (error is not OperationCanceledException) { log?.Invoke("ses-toplama-hatasi", 0, error.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Duygu ayrımından önce toplanmış kesitlerin söyleyişi ölçülür ve her duygu için örnek yapılır (bir kez).
    /// </summary>
    private void UpgradeOldVoices()
    {
        List<Character> old;
        lock (gate) old = characters.Where(c => c.Format < 2 && c.Clips.Count > 0).ToList();
        if (old.Count == 0) return;
        foreach (var character in old)
        {
            var measured = new List<Clip>();
            foreach (var clip in character.Clips)
            {
                if (clip.Measured) { measured.Add(clip); continue; }
                try
                {
                    var audio = SpeechAudio.FromWav(File.ReadAllBytes(Path.Combine(ClipsDirectory, clip.File)));
                    var prosody = Prosody.Measure(To16k(audio.Samples));
                    measured.Add(clip with
                    {
                        Loudness = prosody.Loudness == 0 ? -0.01 : prosody.Loudness,
                        Pitch = prosody.HasPitch ? prosody.Pitch : 0,
                        Spread = prosody.HasPitch ? prosody.Spread : 0,
                    });
                }
                catch (Exception error) when (error is IOException or InvalidDataException) { }
            }
            lock (gate)
            {
                character.Clips = measured;
                character.Usual = Baseline.Of(measured);
                BuildPromptLocked(character);
            }
        }
        var merged = 0;
        lock (gate)
        {
            // Bölünmüş karakterler de birleşir (birleşme bir öncekini değiştirebilir; değişiklik bitene kadar).
            for (var again = true; again;)
            {
                again = false;
                foreach (var character in characters.ToList())
                    if (characters.Contains(character) && MergeSimilarLocked(character) is not null) { merged++; again = true; }
            }
            Save();
        }
        log?.Invoke("ses-duygu-hazirlandi", 0, $"{old.Count} karakterin kesitleri duygu örneklerine ayrıldı, {merged} bölünmüş karakter birleşti");
        Changed?.Invoke();
    }

    private void Collect(long startedAt, double seconds, string? speaker, string text)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (Audio.Slice(startedAt, startedAt + (long)(seconds * 1000)) is not { } raw) return;
        var quality = Quality.Measure(raw, GameAudioRing.Rate);
        if (!quality.Usable)
        {
            log?.Invoke("ses-kesit-atildi", clock.ElapsedMilliseconds, $"{quality} | {text}");
            return;
        }
        var clip = raw[quality.Start..quality.End];
        var clip16 = To16k(clip);
        if (embedder.Embed(clip16) is not { } embedding) return;
        var prosody = Prosody.Measure(clip16);

        Character character;
        double similarity;
        bool created;
        double mood;
        lock (gate)
        {
            (character, similarity, created) = Assign(embedding, speaker, prosody.HasPitch ? prosody.Pitch : 0);
            var file = $"c{character.Id}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.wav";
            Directory.CreateDirectory(ClipsDirectory);
            File.WriteAllBytes(Path.Combine(ClipsDirectory, file), new SpeechAudio(clip, GameAudioRing.Rate).ToWav());
            var added = new Clip(file, quality.Score, clip.Length / (double)GameAudioRing.Rate,
                prosody.Loudness == 0 ? -0.01 : prosody.Loudness, prosody.HasPitch ? prosody.Pitch : 0, prosody.HasPitch ? prosody.Spread : 0);
            character.Clips.Add(added);
            var kept = KeepVariedLocked(character);
            if (kept) BuildPromptLocked(character);
            if (MergeSimilarLocked(character) is { } survivor)
            {
                log?.Invoke("ses-birlesti", 0, $"karakter {character.Label} ile {survivor.Label} aynı kişi; birleştirildi ({survivor.Clips.Count} kesit)");
                character = survivor;
            }
            Save();
            mood = character.Usual.Arousal(added);
        }
        log?.Invoke("ses-kesit", clock.ElapsedMilliseconds,
            $"karakter {character.Label}{(created ? " (yeni)" : $" benzerlik %{similarity * 100:0}")}, {clip.Length / (double)GameAudioRing.Rate:0.0} sn, {quality}, "
            + $"{(prosody.HasPitch ? $"perde {prosody.Pitch:0} Hz ±{prosody.Spread:0.0}, " : "")}coşku {mood:+0.0;-0.0}, örnek {character.PromptSeconds:0.0} sn | {text}");
        Changed?.Invoke();
    }

    private (Character, double, bool) Assign(float[] embedding, string? speaker, double pitch)
    {
        Character? best = null;
        var bestSimilarity = -1.0;
        if (speaker is not null)
        {
            var name = VoiceCast.Key(speaker);
            best = characters.FirstOrDefault(c => c.Name == name);
            if (best is not null) bestSimilarity = Cosine(best.Centroid, embedding);
            else
            {
                // Adsız toplanmış ve sesi çok benzeyen karakter bu addır.
                var unnamed = Nearest(embedding, c => c.Name is null, out var similarity);
                if (unnamed is not null && similarity >= 0.7)
                {
                    unnamed.Name = name;
                    best = unnamed;
                    bestSimilarity = similarity;
                }
            }
            if (best is null)
            {
                var named = NewCharacter(embedding, name);
                return (named, 1, true);
            }
        }
        else
        {
            best = Nearest(embedding, _ => true, out bestSimilarity);
            if (best is null || bestSimilarity < JoinThreshold) return (NewCharacter(embedding, null), 1, true);
            // Ses imzası sınırda benziyor ama perde bambaşka (erkek ve kadın sesi): ayrı karakterdir. İlk oturumda bir
            // karaktere hem 124 hem 210 Hz'lik kesitler girmişti; karışık örnek dublajda başka birinin sesini verir.
            if (bestSimilarity < SurePitchSimilarity && Prosody.Semitones(pitch, MedianPitch(best)) > MaxJoinSemitones)
                return (NewCharacter(embedding, null), 1, true);
        }
        // Ortalama imza yalnız emin olunan kesitlerle güncellenir; yanlış katılan kesit sesi kaydırmasın.
        if (bestSimilarity >= 0.6 || speaker is not null)
        {
            var weight = 1.0 / Math.Min(best.Count + 1, 20);
            for (var i = 0; i < best.Centroid.Length; i++) best.Centroid[i] = (float)(best.Centroid[i] * (1 - weight) + embedding[i] * weight);
            best.Centroid = Normalized(best.Centroid);
        }
        best.Count++;
        return (best, bestSimilarity, false);
    }

    /// <summary>Ses imzası bundan az benzeyen kesit, perdesi de karakterinkinden çok farklıysa o karaktere katılmaz.</summary>
    private const double SurePitchSimilarity = 0.65;
    private const double MaxJoinSemitones = 6;
    /// <summary>İki karakter, ses imzaları bu kadar benzer ve perdeleri yakınsa aynı kişidir (bkz. MergeSimilarLocked).</summary>
    private const double MergeSimilarity = 0.45;
    private const double MergeSemitones = 4;

    /// <summary>Kesitlerin ortanca perdesi (Hz); bilinmiyorsa 0.</summary>
    private static double MedianPitch(Character character)
    {
        var pitches = character.Clips.Where(c => c.Pitch > 0).Select(c => c.Pitch).Order().ToList();
        return pitches.Count == 0 ? 0 : pitches[pitches.Count / 2];
    }

    /// <summary>
    /// Aynı kişi ilk kesitlerinde iki karaktere bölünebiliyor (ilk oturumda Jason ve Lucia'nın birer kesiti ayrı
    /// karakter olmuştu). Ses imzası benzer ve perdesi yakın karakterler birleşir: azı çoğuna katılır. Adı olan iki
    /// farklı karakter birleşmez. Birleştiyse kalan karakter döner.
    /// </summary>
    private Character? MergeSimilarLocked(Character changed)
    {
        foreach (var other in characters)
        {
            if (ReferenceEquals(other, changed)) continue;
            if (changed.Name is not null && other.Name is not null) continue;
            if (Cosine(changed.Centroid, other.Centroid) < MergeSimilarity) continue;
            var (a, b) = (MedianPitch(changed), MedianPitch(other));
            if (a <= 0 || b <= 0 || Prosody.Semitones(a, b) > MergeSemitones) continue;
            var (keep, gone) = other.Count >= changed.Count ? (other, changed) : (changed, other);
            var total = keep.Count + gone.Count;
            for (var i = 0; i < keep.Centroid.Length; i++)
                keep.Centroid[i] = (float)(((keep.Centroid[i] * keep.Count) + (gone.Centroid[i] * gone.Count)) / total);
            keep.Centroid = Normalized(keep.Centroid);
            keep.Count = total;
            keep.Name ??= gone.Name;
            keep.Clips.AddRange(gone.Clips);
            foreach (var mood in Moods) TryDelete(Path.Combine(PromptsDirectory, PromptFile(gone, mood.Name, gone.Version)));
            characters.Remove(gone);
            KeepVariedLocked(keep);
            BuildPromptLocked(keep);
            return keep;
        }
        return null;
    }

    private Character NewCharacter(float[] embedding, string? name)
    {
        if (characters.Count >= MaxCharacters)
        {
            // En az kesiti olan, en eski karakter yer açar.
            var weakest = characters.OrderBy(c => c.Count).ThenBy(c => c.Id).First();
            foreach (var clip in weakest.Clips) TryDelete(Path.Combine(ClipsDirectory, clip.File));
            characters.Remove(weakest);
        }
        var character = new Character
        {
            Id = characters.Count == 0 ? 1 : characters.Max(c => c.Id) + 1,
            Name = name,
            Centroid = (float[])embedding.Clone(),
            Count = 1,
        };
        characters.Add(character);
        return character;
    }

    private Character? Nearest(float[] embedding, Func<Character, bool> filter, out double similarity)
    {
        Character? best = null;
        similarity = -1;
        foreach (var character in characters)
        {
            if (!filter(character)) continue;
            var s = Cosine(character.Centroid, embedding);
            if (s > similarity) { similarity = s; best = character; }
        }
        return best;
    }

    /// <summary>
    /// Satırın sesi: altyazıda adı geçen karakterin, yoksa (<paramref name="identify"/>) son iki saniyede konuşanın
    /// sesi tanınırsa onun örneği. Hazır değilse null; dublaj hazır seslerle sürer.
    /// </summary>
    public VoicePrompt? VoiceFor(SpokenLine line, bool identify)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Character? character = null;
        lock (gate)
        {
            if (line.Speaker is { } speaker && characters.FirstOrDefault(c => c.Name == VoiceCast.Key(speaker)) is { } named)
            {
                if (!named.Ready) return null;
                character = named;
            }
        }
        if (character is null && !identify) return null;

        // Satırın İngilizcesi şu an (ya da az önce) oyunda söyleniyor: son iki saniyesi hem konuşanı tanımak hem de
        // nasıl söylendiğini (bağırarak mı, sakin mi) ölçmek için dinlenir.
        float[]? speech16 = null;
        var now = Audio.Now;
        if (Audio.Slice(now - 2200, now) is { } recent && !stop.IsCancellationRequested)
        {
            var quality = Quality.Measure(recent, GameAudioRing.Rate, relaxed: true);
            if (quality.SpeechSeconds >= 0.8) speech16 = To16k(recent[quality.Start..quality.End]);
        }
        double similarity = 1;
        if (character is null)
        {
            if (speech16 is null) return null;
            float[]? embedding;
            try { embedding = embedder.Embed(speech16); }
            catch (ObjectDisposedException) { return null; }
            if (embedding is null) return null;
            lock (gate)
            {
                character = Nearest(embedding, c => c.Ready, out similarity);
                if (character is null || similarity < MatchThreshold)
                {
                    log?.Invoke("ses-tanima", clock.ElapsedMilliseconds, character is null ? "hazır karakter yok"
                        : $"karakter {character.Label} benzerlik %{similarity * 100:0} (yetersiz) | {line.Turkish}");
                    return null;
                }
            }
        }

        var prosody = speech16 is null ? default : Prosody.Measure(speech16);
        var fromText = LineArousal(line);
        double fromVoice;
        lock (gate) fromVoice = prosody.HasPitch ? character.Usual.Arousal(prosody.Loudness, prosody.Pitch, prosody.Spread, useLoudness: false) : 0;
        // Ses ölçülebildiyse esas odur; yazı ipucu yönü belirgin olduğunda ekler (bağıran satırda ünlem de olur).
        var arousal = prosody.HasPitch ? (fromVoice * 0.7) + (fromText * 0.5) : fromText;
        var mood = arousal >= MoodThreshold ? VoiceMood.Intense : arousal <= -MoodThreshold ? VoiceMood.Calm : VoiceMood.Normal;
        lock (gate)
        {
            log?.Invoke("ses-tanima", clock.ElapsedMilliseconds,
                $"karakter {character.Label}{(similarity < 1 ? $" benzerlik %{similarity * 100:0}" : "")}, duygu {Moods.First(m => m.Mood == mood).Name} "
                + $"(ses {(prosody.HasPitch ? fromVoice.ToString("+0.0;-0.0") : "yok")}, yazı {fromText:+0.0;-0.0}) | {line.Turkish}");
            return PromptLocked(character, mood);
        }
    }

    private VoicePrompt? PromptLocked(Character character, VoiceMood mood)
    {
        var name = Moods.First(m => m.Mood == mood).Name;
        var fileName = PromptFile(character, name, character.Version);
        var path = "/voices/klon/" + fileName;
        var fallback = mood == VoiceMood.Normal ? null : PromptLocked(character, VoiceMood.Normal);
        if (!promptCache.TryGetValue(path, out var wav))
        {
            try { wav = File.ReadAllBytes(Path.Combine(PromptsDirectory, fileName)); }
            catch (IOException) { return fallback; }
            promptCache[path] = wav;
        }
        return new VoicePrompt(path, wav, mood == VoiceMood.Normal ? character.Label : $"{character.Label} {name}",
            character.PromptPitch.GetValueOrDefault(name), fallback);
    }

    private readonly Dictionary<string, int> failures = new(StringComparer.Ordinal);

    /// <summary>
    /// Örnekle yapılan seslendirme karakterin sesini tutturamadı (perde çok saptı). Aynı örnek iki kez tutmazsa,
    /// içindeki en kısa kesit (kısa ya da arkasında başka ses olan kesit sesi kaydırıyordu) atılıp örnekler yenilenir.
    /// </summary>
    public void PromptFailed(VoicePrompt prompt)
    {
        lock (gate)
        {
            var count = failures[prompt.Path] = failures.GetValueOrDefault(prompt.Path) + 1;
            log?.Invoke("ses-tutmadi", 0, $"{prompt.Character}: {prompt.Path} ({count}. kez)");
            if (count < 2) return;
            var file = prompt.Path.Split('/')[^1];
            var character = characters.FirstOrDefault(c => Moods.Any(m => PromptFile(c, m.Name, c.Version) == file));
            if (character is null || character.Clips.Count <= 2) return;
            var mood = Moods.First(m => PromptFile(character, m.Name, character.Version) == file).Name;
            var suspects = character.PromptClips.GetValueOrDefault(mood) ?? [];
            var dropped = character.Clips.Where(c => suspects.Contains(c.File)).OrderBy(c => c.Seconds).FirstOrDefault();
            if (dropped is null) return;
            character.Clips.Remove(dropped);
            TryDelete(Path.Combine(ClipsDirectory, dropped.File));
            character.Usual = Baseline.Of(character.Clips);
            BuildPromptLocked(character);
            Save();
            log?.Invoke("ses-kesit-cikarildi", 0, $"karakter {character.Label}: {dropped.File} ({dropped.Seconds:0.0} sn) sesi kaydırıyordu");
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Satırın coşkusu yazıdan: çeviren yapay zekâ sahneye bakıp söyleyiş gücünü ve duyguyu seçtiyse onlar
    /// (bağırış +1, fısıltı −1; kızgın, şaşkın +; üzgün, sakin −), seçmediyse İngilizcedeki ipuçları.
    /// </summary>
    public static double LineArousal(SpokenLine line)
    {
        if (line.Intensity is null && line.Emotion is null) return TextArousal(line.English);
        var score = line.Intensity switch { SpeechIntensity.Loud => 1.0, SpeechIntensity.Quiet => -1.0, _ => 0.0 };
        score += line.Emotion switch
        {
            SpeechEmotion.Angry => 0.5,
            SpeechEmotion.Surprised => 0.4,
            SpeechEmotion.Happy => 0.2,
            SpeechEmotion.Sad => -0.4,
            SpeechEmotion.Calm => -0.5,
            _ => 0.0,
        };
        return Math.Clamp(score, -1.5, 1.5);
    }

    /// <summary>
    /// Satırın yazısındaki duygu ipuçları: ünlem, büyük harfle bağırma, küfür ve emir (+); üç nokta, fısıltı (−).
    /// Oyun sesi ölçülemezse (müzik, sesi kısılmış konuşma) yalnız bunlara bakılır.
    /// </summary>
    public static double TextArousal(string english)
    {
        if (string.IsNullOrWhiteSpace(english)) return 0;
        var score = 0.0;
        var exclamations = english.Count(c => c == '!');
        if (exclamations > 0) score += exclamations > 1 ? 1.0 : 0.7;
        var letters = english.Where(char.IsLetter).ToList();
        if (letters.Count >= 6 && letters.Count(char.IsUpper) > letters.Count * 0.7) score += 0.6;
        var lower = english.ToLowerInvariant();
        if (Shouted.IsMatch(lower)) score += 0.4;
        if (lower.Contains("...") || lower.Contains('…')) score -= 0.4;
        if (Whispered.IsMatch(lower)) score -= 1.2;
        return Math.Clamp(score, -1.5, 1.5);
    }

    private static readonly System.Text.RegularExpressions.Regex Shouted = new(
        @"\b(fuck\w*|shit|goddamn|motherfuck\w*|get down|get out|watch out|look out|move|run|go go|hurry|help|stop|shut up|freeze|drop it|now)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex Whispered = new(
        @"\((whisper\w*|quietly|softly|sighs?)\)|\[(whisper\w*|quietly|softly|sighs?)\]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Kesit sayısı sınırı aşınca, söyleyişi (coşkusu) en çok benzeyen iki kesitten temizliği düşük olan atılır:
    /// böylece en temiz kesitler kadar sakin ve bağırarak söylenmiş olanlar da kalır. Yeni kesit kaldıysa true.
    /// </summary>
    private bool KeepVariedLocked(Character character)
    {
        var newest = character.Clips[^1];
        character.Usual = Baseline.Of(character.Clips);
        while (character.Clips.Count > MaxClips)
        {
            var ordered = character.Clips.OrderBy(character.Usual.Arousal).ToList();
            var closest = 1;
            for (var i = 2; i < ordered.Count; i++)
                if (character.Usual.Arousal(ordered[i]) - character.Usual.Arousal(ordered[i - 1])
                    < character.Usual.Arousal(ordered[closest]) - character.Usual.Arousal(ordered[closest - 1]))
                    closest = i;
            var dropped = ordered[closest].Score < ordered[closest - 1].Score ? ordered[closest] : ordered[closest - 1];
            character.Clips.Remove(dropped);
            TryDelete(Path.Combine(ClipsDirectory, dropped.File));
            character.Usual = Baseline.Of(character.Clips);
        }
        character.Clips.Sort((a, b) => b.Score.CompareTo(a.Score));
        return character.Clips.Contains(newest);
    }

    private static readonly (VoiceMood Mood, string Name, double Target)[] Moods =
        [(VoiceMood.Calm, "sakin", -1), (VoiceMood.Normal, "normal", 0), (VoiceMood.Intense, "gergin", 1)];

    private static string PromptFile(Character character, string mood, int version) => $"c{character.Id}-{mood}-v{version}.wav";

    /// <summary>
    /// Her duygu için ayrı örnek: kesitler, söyleyişleri o duyguya (sakin −1, olağan 0, gergin +1) yakınlıklarına göre
    /// sıralanıp ~9 sn birleştirilir. pocket-tts sesi olduğu kadar söyleyişi de örnekten aldığı için gergin satır,
    /// karakterin gergin konuştuğu kesitlerle okunur.
    /// </summary>
    private void BuildPromptLocked(Character character)
    {
        if (character.Clips.Count == 0) return;
        var previous = character.Version;
        character.Version++;
        character.Format = 2;
        Directory.CreateDirectory(PromptsDirectory);
        foreach (var (_, name, target) in Moods)
        {
            var order = character.Clips
                .OrderBy(c => Math.Abs(character.Usual.Arousal(c) - target) - (c.Score * 0.5))
                .ToList();
            var used = new List<string>();
            var joined = JoinClips(order, out var seconds, used);
            if (joined is null) continue;
            if (target == 0) character.PromptSeconds = seconds;
            character.PromptClips[name] = used;
            var pitch = Prosody.Measure(new SpeechAudio(joined, GameAudioRing.Rate));
            character.PromptPitch[name] = pitch.HasPitch ? pitch.Pitch : 0;
            File.WriteAllBytes(Path.Combine(PromptsDirectory, PromptFile(character, name, character.Version)),
                new SpeechAudio(joined, GameAudioRing.Rate).ToWav());
            TryDelete(Path.Combine(PromptsDirectory, PromptFile(character, name, previous)));
            promptCache.Remove("/voices/klon/" + PromptFile(character, name, previous));
        }
        // Eski biçimdeki tek örnek.
        TryDelete(Path.Combine(PromptsDirectory, $"c{character.Id}-v{previous}.wav"));
    }

    /// <summary>Kesitler arka arkaya (araya çeyrek saniye sessizlik), en çok 12 sn; ses seviyesi eşitlenir.</summary>
    private float[]? JoinClips(IEnumerable<Clip> clips, out double total, List<string> used)
    {
        total = 0;
        var parts = new List<float[]>();
        double seconds = 0;
        foreach (var clip in clips)
        {
            if (seconds >= TargetPromptSeconds) break;
            float[] samples;
            try { samples = SpeechAudio.FromWav(File.ReadAllBytes(Path.Combine(ClipsDirectory, clip.File))).Samples; }
            catch (Exception error) when (error is IOException or InvalidDataException) { continue; }
            var room = (int)((MaxPromptSeconds - seconds) * GameAudioRing.Rate);
            if (room < GameAudioRing.Rate) break;
            if (samples.Length > room) samples = samples[..room];
            parts.Add(Normalize(samples));
            used.Add(clip.File);
            seconds += samples.Length / (double)GameAudioRing.Rate + 0.25;
        }
        if (parts.Count == 0) return null;
        var gap = new float[GameAudioRing.Rate / 4];
        var joined = parts.SelectMany((part, i) => i == 0 ? part : gap.Concat(part)).ToArray();
        total = joined.Length / (double)GameAudioRing.Rate;
        return joined;
    }

    /// <summary>Tepe seviyesi −1 dB'ye; baş ve sonda 10 ms yumuşak geçiş.</summary>
    private static float[] Normalize(float[] samples)
    {
        var peak = samples.Length == 0 ? 0 : samples.Max(Math.Abs);
        var gain = peak > 1e-4f ? 0.89f / peak : 1f;
        var result = new float[samples.Length];
        var fade = Math.Min(GameAudioRing.Rate / 100, samples.Length / 2);
        for (var i = 0; i < samples.Length; i++)
        {
            var envelope = i < fade ? i / (float)fade : i >= samples.Length - fade ? (samples.Length - 1 - i) / (float)fade : 1f;
            result[i] = samples[i] * gain * envelope;
        }
        return result;
    }

    /// <summary>24 kHz → 16 kHz (doğrusal ara değer; ses imzası için yeterli).</summary>
    public static float[] To16k(ReadOnlySpan<float> samples24k)
    {
        var length = samples24k.Length * 2 / 3;
        var result = new float[length];
        for (var i = 0; i < length; i++)
        {
            var position = i * 1.5;
            var index = (int)position;
            var fraction = (float)(position - index);
            var next = Math.Min(index + 1, samples24k.Length - 1);
            result[i] = samples24k[index] * (1 - fraction) + samples24k[next] * fraction;
        }
        return result;
    }

    public static float[] Normalized(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector) sum += value * value;
        var length = Math.Sqrt(sum);
        return length < 1e-9 ? vector : [.. vector.Select(value => (float)(value / length))];
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return -1;
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    /// <summary>Toplanan bütün sesleri siler.</summary>
    public void Clear()
    {
        lock (gate)
        {
            characters.Clear();
            promptCache.Clear();
            heard.Clear();
            shown.Clear();
            waiting.Clear();
            taken.Clear();
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
        Changed?.Invoke();
    }

    /// <summary>Oyun adından klasör adı ("Grand Theft Auto VI" → "grand-theft-auto-vi"; çeviri hafızasıyla aynı).</summary>
    public static string FolderName(string? game)
    {
        var chars = (game ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : c switch
            {
                'ç' => 'c', 'ğ' => 'g', 'ı' => 'i', 'ö' => 'o', 'ş' => 's', 'ü' => 'u',
                _ => '-',
            }).ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? "genel" : slug;
    }

    /// <summary>Bir oyunun toplanan seslerini, oturum açık değilken siler.</summary>
    public static void Delete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    /// <summary>Klasördeki hazır karakter sayısı (oturum açık değilken ayarlarda göstermek için).</summary>
    public static int CountReady(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "karakterler.json");
            if (!File.Exists(path)) return 0;
            var saved = JsonSerializer.Deserialize<List<Character>>(File.ReadAllText(path), Json) ?? [];
            return saved.Count(c => c.Ready);
        }
        catch (Exception error) when (error is IOException or JsonException) { return 0; }
    }

    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel();
        work.Writer.TryComplete();
        // Model, süren kesit işlenince bırakılır.
        Stopped.ContinueWith(_ => embedder.Dispose(), TaskScheduler.Default);
    }

    private string ClipsDirectory => Path.Combine(directory, "kesitler");
    private string PromptsDirectory => Path.Combine(directory, "ornekler");
    private string IndexPath => Path.Combine(directory, "karakterler.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private void Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return;
            var saved = JsonSerializer.Deserialize<List<Character>>(File.ReadAllText(IndexPath), Json);
            if (saved is not null) characters.AddRange(saved.Where(c => c.Centroid.Length > 0));
        }
        catch (Exception error) when (error is IOException or JsonException) { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(directory);
            var temporary = IndexPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(characters, Json));
            File.Move(temporary, IndexPath, overwrite: true);
        }
        catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public sealed class Character
    {
        public int Id { get; set; }
        /// <summary>Altyazıdaki konuşmacı adı (büyük harf); GTA gibi adsız oyunlarda null.</summary>
        public string? Name { get; set; }
        public float[] Centroid { get; set; } = [];
        public int Count { get; set; }
        public List<Clip> Clips { get; set; } = [];
        public int Version { get; set; }
        public double PromptSeconds { get; set; }
        /// <summary>Örneklerin biçimi: 2 = her duygu için ayrı örnek (sakin, normal, gergin).</summary>
        public int Format { get; set; }
        /// <summary>Karakterin olağan söyleyişi (kesitlerin ortalaması ve yayılımı): duygu buna göre ölçülür.</summary>
        public Baseline Usual { get; set; } = new();
        /// <summary>Her duygu örneğinin perdesi (Hz) ve hangi kesitlerden yapıldığı.</summary>
        public Dictionary<string, double> PromptPitch { get; set; } = [];
        public Dictionary<string, List<string>> PromptClips { get; set; } = [];
        /// <summary>Tek uzun kesit yetmez: başka bir karakterin yanlış ayrılmış tek repliği "hazır" sayılıyordu.</summary>
        public bool Ready => Version > 0 && PromptSeconds >= MinPromptSeconds && Clips.Count >= 2;
        public string Label => Name ?? $"#{Id}";
    }

    /// <param name="Loudness">Konuşmanın ses yüksekliği (dB); 0: ölçülmedi (eski kesit).</param>
    /// <param name="Pitch">Perde (Hz, ortanca); 0: bulunamadı.</param>
    /// <param name="Spread">Perdenin oynaklığı (yarım ton).</param>
    public sealed record Clip(string File, double Score, double Seconds, double Loudness = 0, double Pitch = 0, double Spread = 0)
    {
        public bool Measured => Loudness != 0;
    }

    /// <summary>Bir karakterin olağan ses yüksekliği, perdesi (log Hz) ve oynaklığı: ortalama ve yayılım.</summary>
    public sealed class Baseline
    {
        public double LoudMean { get; set; }
        public double LoudDev { get; set; } = 1;
        public double PitchMean { get; set; }
        public double PitchDev { get; set; } = 1;
        public double SpreadMean { get; set; }
        public double SpreadDev { get; set; } = 1;

        public static Baseline Of(IReadOnlyList<Clip> clips)
        {
            var measured = clips.Where(c => c.Measured).ToList();
            var voiced = measured.Where(c => c.Pitch > 0).ToList();
            var (loudMean, loudDev) = Stats(measured.Select(c => c.Loudness), 3);
            var (pitchMean, pitchDev) = Stats(voiced.Select(c => Math.Log2(c.Pitch) * 12), 1.5);
            var (spreadMean, spreadDev) = Stats(voiced.Select(c => c.Spread), 0.6);
            return new Baseline
            {
                LoudMean = loudMean, LoudDev = loudDev, PitchMean = pitchMean, PitchDev = pitchDev,
                SpreadMean = spreadMean, SpreadDev = spreadDev,
            };
        }

        /// <summary>Ortalama ve yayılım; yayılım, az kesitle sıfıra inip her farkı abartmasın diye en az <paramref name="floor"/>.</summary>
        private static (double, double) Stats(IEnumerable<double> values, double floor)
        {
            var list = values.ToList();
            if (list.Count == 0) return (0, floor);
            var mean = list.Average();
            var dev = Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / list.Count);
            return (mean, Math.Max(dev, floor));
        }

        /// <summary>
        /// Olağandan ne kadar coşkulu (+) ya da sakin (−): yükseklik, perde ve oynaklığın sapmalarının ortalaması.
        /// Ses yüksekliği yalnız kesitlerde kullanılır; dublaj sırasında oyunun konuşma sesi kısılmış olabilir.
        /// </summary>
        public double Arousal(double loudness, double pitch, double spread, bool useLoudness)
        {
            var parts = new List<double>();
            if (useLoudness && loudness != 0) parts.Add((loudness - LoudMean) / LoudDev);
            if (pitch > 0)
            {
                parts.Add(((Math.Log2(pitch) * 12) - PitchMean) / PitchDev);
                parts.Add((spread - SpreadMean) / SpreadDev);
            }
            return parts.Count == 0 ? 0 : Math.Clamp(parts.Average(), -2.5, 2.5);
        }

        public double Arousal(Clip clip) => Arousal(clip.Loudness, clip.Pitch, clip.Spread, useLoudness: true);
    }

    /// <summary>Kesitin konuşma bölümü ve temizliği.</summary>
    public readonly record struct Quality(int Start, int End, double SpeechSeconds, double SnrDb, double Clipped, double Score, bool Usable)
    {
        private const int Frame = GameAudioRing.Rate / 50; // 20 ms

        /// <summary>
        /// Gürültü tabanı (en sessiz %10) ile konuşma seviyesi (en yüksek %10) arasındaki fark: müzik ya da patlama
        /// konuşmanın altında sürekliyse fark küçülür ve kesit atılır. Baştaki ve sondaki sessizlik kırpılır.
        /// </summary>
        public static Quality Measure(float[] samples, int rate, bool relaxed = false)
        {
            var frames = samples.Length / Frame;
            if (frames < 10) return new Quality(0, samples.Length, 0, 0, 0, 0, false);
            var levels = new double[frames];
            var clipped = 0;
            for (var f = 0; f < frames; f++)
            {
                double sum = 0;
                for (var i = f * Frame; i < (f + 1) * Frame; i++)
                {
                    sum += samples[i] * samples[i];
                    if (Math.Abs(samples[i]) > 0.99f) clipped++;
                }
                levels[f] = Math.Sqrt(sum / Frame);
            }
            var sorted = levels.Order().ToArray();
            var floor = Math.Max(sorted[frames / 10], 1e-5);
            var loud = sorted[Math.Min(frames - 1, frames * 9 / 10)];
            var snr = 20 * Math.Log10(loud / floor);
            var threshold = Math.Max(floor * 3, loud * 0.1);
            int first = -1, last = -1, speech = 0;
            for (var f = 0; f < frames; f++)
            {
                if (levels[f] < threshold) continue;
                speech++;
                if (first < 0) first = f;
                last = f;
            }
            if (first < 0 || loud < 0.005) return new Quality(0, samples.Length, 0, snr, 0, 0, false);
            var start = Math.Max(0, (first - 3) * Frame);
            var end = Math.Min(samples.Length, (last + 4) * Frame);
            var speechSeconds = speech * Frame / (double)rate;
            var clippedShare = clipped / (double)samples.Length;
            var score = Math.Clamp(snr, 0, 40) / 40 * Math.Min(speechSeconds, 6) / 6;
            var usable = relaxed || (snr >= 14 && speechSeconds >= 1.0 && clippedShare < 0.005);
            return new Quality(start, end, speechSeconds, snr, clippedShare, score, usable);
        }

        public override string ToString() => $"temizlik {SnrDb:0} dB, konuşma {SpeechSeconds:0.0} sn, puan {Score:0.00}";
    }
}

/// <param name="Path">Altyazı sunucusundaki adresi (pocket-tts buradan alır; sürüm değişince adres de değişir).</param>
/// <param name="Pitch">Örneğin perdesi (Hz; 0: bilinmiyor). Seslendirme bundan çok saparsa ses tutmamıştır.</param>
/// <param name="Fallback">Ses tutmazsa denenecek örnek (aynı karakterin olağan örneği); yoksa hazır ses.</param>
public sealed record VoicePrompt(string Path, byte[] Wav, string Character, double Pitch = 0, VoicePrompt? Fallback = null)
{
    /// <summary>
    /// Seslendirme bu kadar yarım tondan çok saparsa karakterin sesi tutmamıştır. Denemede kısa ya da arkasında başka
    /// ses olan bir kesitten yapılan örnek, 118 Hz'lik erkek sesini aynı cümlede bir 140, bir 222 Hz okudu; sağlam
    /// örneklerde sapma 3 yarım tonu geçmedi.
    /// </summary>
    public const double MaxDrift = 4.5;

    public bool Matches(SpeechAudio spoken)
    {
        if (Pitch <= 0) return true;
        var measured = Prosody.Measure(spoken);
        return !measured.HasPitch || Prosody.Semitones(measured.Pitch, Pitch) <= MaxDrift;
    }
}
