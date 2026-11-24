// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core.Dubbing;
using Xunit;

namespace CouchSubs.Core.Tests;

public class CharacterVoicesTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "couchsubs-voices-" + Guid.NewGuid().ToString("N"));
    private long now = 100_000;

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>Ses imzası: sesin perdesi (frekansı) — iki "karakter" iki farklı perdede konuşur.</summary>
    private sealed class PitchEmbedder : ISpeakerEmbedder
    {
        public float[]? Embed(ReadOnlySpan<float> samples16k)
        {
            if (samples16k.Length < 8000) return null;
            var crossings = 0;
            for (var i = 1; i < samples16k.Length; i++)
                if (samples16k[i - 1] < 0 && samples16k[i] >= 0) crossings++;
            var hz = crossings * 16000.0 / samples16k.Length;
            return CharacterVoices.Normalized(hz < 160 ? [1f, 0.1f] : [0.1f, 1f]);
        }

        public void Dispose() { }
    }

    /// <summary>Konuşma: yarım saniye sessizlik, ardından sinüs (hece gibi aralıklı), sonra sessizlik.</summary>
    private static float[] Speech(double hz, double seconds)
    {
        var rate = 48000;
        var samples = new float[(int)((seconds + 1) * rate)];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)rate;
            if (t < 0.5 || t > seconds + 0.5) { samples[i] = 0.0005f; continue; } // sessizlik (sıfırdan geçmez)
            var syllable = Math.Sin(2 * Math.PI * 3 * t) > -0.6 ? 1.0 : 0.15;
            samples[i] = (float)(0.4 * syllable * Math.Sin(2 * Math.PI * hz * t));
        }
        return samples;
    }

    public readonly List<string> Log = [];

    private CharacterVoices Make()
    {
        var ring = new GameAudioRing(() => now);
        return new CharacterVoices(folder, new PitchEmbedder(), (kind, ms, text) => { lock (Log) Log.Add($"{kind} | {text}"); }, ring);
    }

    /// <summary>Oyun bir replik söyler: ses tampona yazılır, Whisper metni ve altyazı gelir.</summary>
    private void Say(CharacterVoices voices, double hz, double seconds, string english, string? speaker = null)
    {
        var started = now;
        var audio = Speech(hz, seconds);
        for (var offset = 0; offset < audio.Length; offset += 960)
        {
            now += 20;
            voices.Audio.Feed(audio.AsSpan(offset, Math.Min(960, audio.Length - offset)), 48000);
        }
        voices.Shown([new SpokenLine(english, "tr", speaker)]);
        voices.Heard(english.ToLowerInvariant().TrimEnd('.'), started, seconds + 1);
    }

    private static void WaitFor(Func<bool> condition, Func<string>? explain = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(20);
        Assert.True(condition(), explain?.Invoke());
    }

    [Fact]
    public void SeparatesCharactersByVoiceAndBuildsPrompts()
    {
        using var voices = Make();
        Say(voices, 120, 3, "We have to get out of here right now.");
        Say(voices, 220, 3, "The car is parked behind the warehouse.");
        Say(voices, 125, 3, "Grab the bag and follow me quickly.");
        Say(voices, 215, 3, "Nobody followed us here, I promise you.");
        WaitFor(() => voices.ReadyCount == 2, () => string.Join("\n", Log));
        Assert.Equal(2, voices.KnownCount);

        // Dublaj sırasında son konuşan tanınır: kadın perdesi → ikinci karakter.
        var audio = Speech(220, 2);
        for (var offset = 0; offset < audio.Length; offset += 960)
        {
            now += 20;
            voices.Audio.Feed(audio.AsSpan(offset, Math.Min(960, audio.Length - offset)), 48000);
        }
        now -= 400; // son yarım saniye sessizlik değil, konuşma olsun
        var prompt = voices.VoiceFor(new SpokenLine("Let's go.", "Gidelim."), identify: true);
        Assert.NotNull(prompt);
        Assert.Equal("#2", prompt!.Character);
        Assert.StartsWith("/voices/klon/c2-v", prompt.Path);
        var wav = SpeechAudio.FromWav(prompt.Wav);
        Assert.True(wav.Duration >= CharacterVoices.MinPromptSeconds);
    }

    [Fact]
    public void UsesSpeakerNameAndKeepsVoicesForNextSession()
    {
        using (var voices = Make())
        {
            Say(voices, 120, 3, "We have to get out of here right now.", speaker: "Price");
            Say(voices, 120, 3, "Grab the bag and follow me quickly.", speaker: "Price");
            WaitFor(() => voices.ReadyCount == 1);
        }
        Assert.Equal(1, CharacterVoices.CountReady(folder));
        using var again = Make();
        var prompt = again.VoiceFor(new SpokenLine("Move.", "Yürü.", "PRICE"), identify: false);
        Assert.Equal("PRICE", prompt?.Character);
    }

    [Fact]
    public void IgnoresSpeechThatDoesNotMatchTheSubtitle()
    {
        using var voices = Make();
        var started = now;
        var audio = Speech(120, 3);
        for (var offset = 0; offset < audio.Length; offset += 960)
        {
            now += 20;
            voices.Audio.Feed(audio.AsSpan(offset, Math.Min(960, audio.Length - offset)), 48000);
        }
        voices.Shown([new SpokenLine("We have to get out of here right now.", "tr")]);
        voices.Heard("i'll see you next time thanks for watching", started, 4);
        Thread.Sleep(300);
        Assert.Equal(0, voices.KnownCount);
    }

    [Fact]
    public void OverlapPenalizesExtraWords()
    {
        Assert.Equal(1, CharacterVoices.Overlap("we have to go", "we have to go"));
        Assert.True(CharacterVoices.Overlap("we have to go now move it come on", "we have to go") < 0.6);
    }
}
