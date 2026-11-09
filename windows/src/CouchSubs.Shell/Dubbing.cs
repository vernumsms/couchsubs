// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;
using CouchSubs.Core;
using CouchSubs.Core.Dubbing;

namespace CouchSubs.Shell;

/// <summary>Dublajın nereden çalınacağı.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DubbingOutput>))]
public enum DubbingOutput
{
    /// <summary>TV uygulaması çalar; ses oyunun sesiyle aynı hoparlörden gelir.</summary>
    Tv,
    /// <summary>Bilgisayarın hoparlörü ya da kulaklığı.</summary>
    Computer,
}

/// <summary>Seslendirme motoru.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DubbingEngine>))]
public enum DubbingEngine
{
    /// <summary>pocket-tts: doğal sesler, bir kez indirilir; işlemciye yük, cümle başına bir iki saniye.</summary>
    Natural,
    /// <summary>Windows'un kendi Türkçe sesi: indirme yok, anında konuşur, daha mekanik.</summary>
    Fast,
}

public sealed record DubbingSettings
{
    public bool Enabled { get; init; }
    public DubbingEngine Engine { get; init; } = DubbingEngine.Natural;
    public DubbingOutput Output { get; init; } = DubbingOutput.Tv;
    public VoiceCast Cast { get; init; } = new();
}

/// <summary>Seslendirme motoru (gerçekte uygulamayla gelen pocket-tts, bkz. <see cref="PocketTtsServer"/>).</summary>
public interface ISpeechSynthesizer : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<SpeechAudio> SynthesizeAsync(string text, Uri voice, CancellationToken cancellationToken = default);
}

/// <summary>Bilgisayardan ses çalar.</summary>
public interface IAudioOutput
{
    void Play(byte[] wav);
    void Stop();
}

/// <summary>Dublaj modeli: kurulu mu, indir, seslerin dosyaları, motor oluştur.</summary>
public interface IDubbingModel
{
    bool IsInstalled { get; }
    long DownloadBytes { get; }
    Task DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken);
    byte[] VoiceFile(DubbingVoice voice);
    ISpeechSynthesizer CreateSynthesizer();
}

/// <summary>
/// Çevrilen cümleleri sırayla seslendirir (Mac: Dubber.swift). Bir cümle çalarken sonraki hazırlanır; oyun konuşması
/// hızlıysa geride kalan eski cümleler atlanır ki ses altyazının çok gerisinde kalmasın.
/// </summary>
public sealed class Dubber(IDubbingModel model, IAudioOutput audio, Func<SubtitleServer> server, Func<int> serverPort, Action<Action> post)
    : IAsyncDisposable
{
    /// <summary>Bekleyen en çok cümle: fazlası eskiden başlayarak atlanır.</summary>
    public const int Backlog = 3;
    private const string PreviewText = "Merhaba, oyundaki karakterleri Türkçe seslendireceğim.";

    private readonly object gate = new();
    private readonly Queue<(SpokenLine Line, DubbingVoice? Preview)> queue = new();
    private ISpeechSynthesizer? synthesizer;
    private bool working;
    private int generation;
    private int Generation { get { lock (gate) return generation; } }
    private SubtitleServer? publishedOn;

    public VoiceCast Cast { get; set; } = new();
    public DubbingOutput Output { get; set; } = DubbingOutput.Tv;
    /// <summary>Yeni bir karaktere ses verildi; ayarlara kaydedilir (arayüz iş parçacığında).</summary>
    public event Action<VoiceCast>? CastChanged;
    public event Action<string>? Failed;

    /// <summary>Model yüklenir, ilk cümle beklemesin.</summary>
    public void Prepare()
    {
        PublishVoices();
        var engine = Synthesizer();
        _ = Task.Run(async () =>
        {
            try { await engine.StartAsync(); }
            catch (Exception error) { post(() => Failed?.Invoke(error.Message)); }
        });
    }

    public void Speak(IReadOnlyList<SpokenLine> lines)
    {
        if (lines.Count == 0) return;
        PublishVoices();
        bool interrupt;
        int number;
        lock (gate)
        {
            // Yeni altyazı geldiyse öncekilerin cümleleri artık geç kalmıştır: bekleyenler atılır, çalan da kesilir.
            // Ses yazının gerisinde kalıp oyunu karıştırmasın; dublaj hep ekrandaki altyazıyı söyler.
            queue.Clear();
            foreach (var line in lines) queue.Enqueue((line, null));
            while (queue.Count > Backlog) queue.Dequeue();
            interrupt = working;
            working = true;
            // Sıra ile kuşak aynı kilit altında değişir; eski döngü yeni cümleyi alamaz.
            number = ++generation;
        }
        if (interrupt)
        {
            audio.Stop();
            server().StopSpeech();
        }
        _ = Task.Run(() => RunAsync(number));
    }

    /// <summary>Ayarlardaki "Dinle": deneme her zaman bilgisayardan çalar, kullanıcı o an bilgisayarın başında.</summary>
    public void Preview(DubbingVoice voice)
    {
        Stop();
        PublishVoices();
        int number;
        lock (gate)
        {
            queue.Enqueue((new SpokenLine("Listen.", PreviewText), voice));
            working = true;
            number = ++generation;
        }
        _ = Task.Run(() => RunAsync(number));
    }

    public void Stop()
    {
        lock (gate)
        {
            queue.Clear();
            working = false;
            generation++;
        }
        audio.Stop();
        server().StopSpeech();
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (synthesizer is { } engine)
        {
            synthesizer = null;
            await engine.DisposeAsync();
        }
    }

    private ISpeechSynthesizer Synthesizer()
    {
        lock (gate) return synthesizer ??= model.CreateSynthesizer();
    }

    private async Task RunAsync(int number)
    {
        Task<(SpeechAudio Audio, bool Preview)?>? next = Next(number);
        while (next is not null)
        {
            var result = await next;
            // Bu arada yeni altyazı geldiyse eski cümle hiç çalınmaz.
            if (number != Generation) return;
            if (result is not { } ready) break;
            // Bu cümle çalarken sonraki hazırlanır.
            next = Next(number);
            await PlayAsync(ready.Audio, ready.Preview ? DubbingOutput.Computer : Output);
            if (number != Generation) return;
        }
        lock (gate)
        {
            if (number != generation) return;
            if (queue.Count == 0) { working = false; return; }
        }
        // Bir cümle seslendirilemediyse sıradakilerle sürer.
        await RunAsync(number);
    }

    private Task<(SpeechAudio, bool)?>? Next(int number)
    {
        SpokenLine line;
        DubbingVoice? preview;
        lock (gate)
        {
            // Geç başlayan eski döngü yeni altyazının cümlesini alıp atmasın.
            if (queue.Count == 0 || number != generation) return null;
            (line, preview) = queue.Dequeue();
        }
        DubbingVoice voice;
        if (preview is { } chosen)
        {
            voice = chosen;
        }
        else
        {
            bool assigned;
            VoiceCast? copy = null;
            lock (gate)
            {
                voice = Cast.VoiceFor(line, out assigned);
                if (assigned) copy = Cast.Copy();
            }
            if (copy is not null) post(() => CastChanged?.Invoke(copy));
        }
        // Yapay zekâ sahneye göre bir ton seçtiyse o, seçmediyse İngilizce metinden tahmin.
        var text = SpeechText.Prepare(line.Turkish, line.Emotion ?? SpeechEmotions.Guess(line.English));
        var url = new Uri($"http://127.0.0.1:{serverPort()}/voices/{voice.FileName()}.wav");
        var engine = Synthesizer();
        return Task.Run<(SpeechAudio, bool)?>(async () =>
        {
            try
            {
                return ((await engine.SynthesizeAsync(text, url)).Tightened(), preview is not null);
            }
            // Motor ne olursa olsun (pocket-tts, Windows sesi) bir cümlenin hatası dublajı durdurmaz.
            catch (Exception error)
            {
                post(() => Failed?.Invoke(error.Message));
                return null;
            }
        });
    }

    private async Task PlayAsync(SpeechAudio speech, DubbingOutput output)
    {
        var wav = speech.ToWav();
        var milliseconds = (int)(speech.Duration * 1000);
        if (output == DubbingOutput.Tv)
        {
            server().SendSpeech(wav, milliseconds);
            // TV sesi ağdan alıp çalmaya biraz geç başlar; sıradaki cümle bunu kesmesin.
            await Task.Delay(milliseconds + 250);
        }
        else
        {
            audio.Play(wav);
            await Task.Delay(milliseconds + 60);
        }
    }

    /// <summary>pocket-tts sesleri altyazı sunucusundan alır (<c>/voices/&lt;ad&gt;.wav</c>).</summary>
    private void PublishVoices()
    {
        var current = server();
        if (ReferenceEquals(publishedOn, current) || !model.IsInstalled) return;
        foreach (var voice in DubbingVoices.All)
        {
            // Windows sesinin örnek dosyası yok; ses türü adresin adından anlaşılır.
            var file = model.VoiceFile(voice);
            if (file.Length > 0) current.Publish(file, $"/voices/{voice.FileName()}.wav");
        }
        publishedOn = current;
    }
}
