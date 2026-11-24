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
    private const int MaxClips = 6;
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
    }

    private readonly Task worker;

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
            }
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
            work.Writer.TryWrite((h.StartedAt, h.Seconds, line.Speaker, line.Key));
        }
    }

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
        if (embedder.Embed(To16k(clip)) is not { } embedding) return;

        Character character;
        double similarity;
        bool created;
        lock (gate)
        {
            (character, similarity, created) = Assign(embedding, speaker);
            var file = $"c{character.Id}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.wav";
            Directory.CreateDirectory(ClipsDirectory);
            File.WriteAllBytes(Path.Combine(ClipsDirectory, file), new SpeechAudio(clip, GameAudioRing.Rate).ToWav());
            character.Clips.Add(new Clip(file, quality.Score, clip.Length / (double)GameAudioRing.Rate));
            character.Clips.Sort((a, b) => b.Score.CompareTo(a.Score));
            while (character.Clips.Count > MaxClips)
            {
                var dropped = character.Clips[^1];
                character.Clips.RemoveAt(character.Clips.Count - 1);
                TryDelete(Path.Combine(ClipsDirectory, dropped.File));
            }
            var promptChanged = character.Clips.Take(4).Any(c => c.File == file);
            if (promptChanged) BuildPromptLocked(character);
            Save();
        }
        log?.Invoke("ses-kesit", clock.ElapsedMilliseconds,
            $"karakter {character.Label}{(created ? " (yeni)" : $" benzerlik %{similarity * 100:0}")}, {clip.Length / (double)GameAudioRing.Rate:0.0} sn, {quality}, örnek {character.PromptSeconds:0.0} sn | {text}");
        Changed?.Invoke();
    }

    private (Character, double, bool) Assign(float[] embedding, string? speaker)
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
        lock (gate)
        {
            if (line.Speaker is { } speaker && characters.FirstOrDefault(c => c.Name == VoiceCast.Key(speaker)) is { } named)
                return named.Ready ? PromptLocked(named) : null;
        }
        if (!identify) return null;
        var now = Audio.Now;
        if (Audio.Slice(now - 2200, now) is not { } recent) return null;
        var quality = Quality.Measure(recent, GameAudioRing.Rate, relaxed: true);
        if (quality.SpeechSeconds < 0.8 || stop.IsCancellationRequested) return null;
        float[]? embedding;
        try { embedding = embedder.Embed(To16k(recent[quality.Start..quality.End])); }
        catch (ObjectDisposedException) { return null; }
        if (embedding is null) return null;
        lock (gate)
        {
            var best = Nearest(embedding, c => c.Ready, out var similarity);
            log?.Invoke("ses-tanima", clock.ElapsedMilliseconds,
                best is null ? "hazır karakter yok" : $"karakter {best.Label} benzerlik %{similarity * 100:0}{(similarity >= MatchThreshold ? "" : " (yetersiz)")} | {line.Turkish}");
            return best is not null && similarity >= MatchThreshold ? PromptLocked(best) : null;
        }
    }

    private VoicePrompt? PromptLocked(Character character)
    {
        var path = $"/voices/klon/c{character.Id}-v{character.Version}.wav";
        if (!promptCache.TryGetValue(path, out var wav))
        {
            var file = Path.Combine(PromptsDirectory, $"c{character.Id}-v{character.Version}.wav");
            try { wav = File.ReadAllBytes(file); }
            catch (IOException) { return null; }
            promptCache[path] = wav;
        }
        return new VoicePrompt(path, wav, character.Label);
    }

    /// <summary>En temiz kesitler arka arkaya (araya çeyrek saniye sessizlik), en çok 12 sn; ses seviyesi eşitlenir.</summary>
    private void BuildPromptLocked(Character character)
    {
        var parts = new List<float[]>();
        double seconds = 0;
        foreach (var clip in character.Clips)
        {
            if (seconds >= TargetPromptSeconds) break;
            float[] samples;
            try { samples = SpeechAudio.FromWav(File.ReadAllBytes(Path.Combine(ClipsDirectory, clip.File))).Samples; }
            catch (Exception error) when (error is IOException or InvalidDataException) { continue; }
            var room = (int)((MaxPromptSeconds - seconds) * GameAudioRing.Rate);
            if (room < GameAudioRing.Rate) break;
            if (samples.Length > room) samples = samples[..room];
            parts.Add(Normalize(samples));
            seconds += samples.Length / (double)GameAudioRing.Rate + 0.25;
        }
        if (parts.Count == 0) return;
        var gap = new float[GameAudioRing.Rate / 4];
        var joined = parts.SelectMany((part, i) => i == 0 ? part : gap.Concat(part)).ToArray();
        var previous = character.Version;
        character.Version++;
        character.PromptSeconds = joined.Length / (double)GameAudioRing.Rate;
        Directory.CreateDirectory(PromptsDirectory);
        File.WriteAllBytes(Path.Combine(PromptsDirectory, $"c{character.Id}-v{character.Version}.wav"), new SpeechAudio(joined, GameAudioRing.Rate).ToWav());
        TryDelete(Path.Combine(PromptsDirectory, $"c{character.Id}-v{previous}.wav"));
        promptCache.Remove($"/voices/klon/c{character.Id}-v{previous}.wav");
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
        worker.ContinueWith(_ => embedder.Dispose(), TaskScheduler.Default);
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
        public bool Ready => Version > 0 && PromptSeconds >= MinPromptSeconds;
        public string Label => Name ?? $"#{Id}";
    }

    public sealed record Clip(string File, double Score, double Seconds);

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
public sealed record VoicePrompt(string Path, byte[] Wav, string Character);
