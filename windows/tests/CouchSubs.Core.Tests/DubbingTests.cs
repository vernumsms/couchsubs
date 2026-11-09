// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using CouchSubs.Core.Dubbing;
using Xunit;

namespace CouchSubs.Core.Tests;

public class SpeechTextTests
{
    [Fact]
    public void EmotionComesFromTheEnglishLine()
    {
        Assert.Equal(SpeechEmotion.Angry, SpeechEmotions.Guess("Get back! This is not your fight."));
        Assert.Equal(SpeechEmotion.Surprised, SpeechEmotions.Guess("What?! The door opened by itself!"));
        Assert.Equal(SpeechEmotion.Sad, SpeechEmotions.Guess("I'm sorry. I couldn't save her."));
        Assert.Equal(SpeechEmotion.Happy, SpeechEmotions.Guess("We did it! We're going home."));
        Assert.Equal(SpeechEmotion.Calm, SpeechEmotions.Guess("Breathe, boy. Take your time."));
        Assert.Null(SpeechEmotions.Guess("We need to cross the river."));
    }

    [Fact]
    public void EverySentenceStartsLowercaseWithTheTagFirst()
    {
        Assert.Equal("[kızgın] geri çekil!", SpeechText.Prepare("Geri çekil!", SpeechEmotion.Angry));
        Assert.Equal("ileri gidelim. hemen...", SpeechText.Prepare("İleri gidelim.  Hemen…"));
        Assert.Equal("ışığı kapat.", SpeechText.Prepare("Işığı kapat."));
        Assert.Equal("ne? kapı açıldı!", SpeechText.Prepare("Ne?! Kapı açıldı!!"));
    }

    [Fact]
    public void NumbersAreSpelledOut()
    {
        Assert.Equal("bin iki yüz elli", TurkishNumbers.Words(1250));
        Assert.Equal("iki milyon yüz beş", TurkishNumbers.Words(2_000_105));
        Assert.Equal("yüz", TurkishNumbers.Words(100));
        Assert.Equal("üç kapı, yüzde yirmi şans, saat on dört otuz, üç virgül beş metre, bin beş yüz altın",
            TurkishNumbers.SpellOut("3 kapı, %20 şans, saat 14:30, 3,5 metre, 1.500 altın"));
        Assert.Equal("dokuz sıfır beş", TurkishNumbers.SpellOut("09:05"));
    }
}

public class VoiceCastTests
{
    [Fact]
    public void EachCharacterKeepsItsVoice()
    {
        var cast = new VoiceCast();
        var kratos = cast.VoiceFor(new SpokenLine("", "", "KRATOS"), out var assigned);
        Assert.True(assigned);
        var freya = cast.VoiceFor(new SpokenLine("", "", "Freya"), out _);
        var atreus = cast.VoiceFor(new SpokenLine("", "", "ATREUS"), out _);
        Assert.Equal(VoiceGender.Male, kratos.Gender());
        Assert.Equal(VoiceGender.Female, freya.Gender());
        Assert.Equal(VoiceGender.Male, atreus.Gender());
        Assert.NotEqual(kratos, atreus);
        Assert.NotEqual(cast.Narrator, kratos);
        Assert.Equal(kratos, cast.VoiceFor(new SpokenLine("", "", "Kratos"), out assigned));
        Assert.False(assigned);
    }

    [Fact]
    public void DashedTurnsAlternateVoices()
    {
        var cast = new VoiceCast();
        var first = cast.VoiceFor(new SpokenLine("", "", NewTurn: true), out _);
        var second = cast.VoiceFor(new SpokenLine("", "", NewTurn: true), out _);
        Assert.NotEqual(first, second);
        Assert.Equal(second, cast.VoiceFor(new SpokenLine("", ""), out _));
    }

    /// Ad listesinde olmayan karaktere yapay zekânın bağlamdan tahmin ettiği cinsiyette ses verilir.
    [Fact]
    public void UnknownNameGetsVoiceOfGuessedGender()
    {
        var cast = new VoiceCast();
        var voice = cast.VoiceFor(new SpokenLine("", "", "ZORVAKKA", Gender: VoiceGender.Female), out _);
        Assert.Equal(VoiceGender.Female, voice.Gender());
    }

    /// Etiketsiz satırda konuşanın cinsiyeti biliniyorsa anlatıcı sesi yerine ona uyan ses okunur.
    [Fact]
    public void UnlabeledLineFollowsGuessedGender()
    {
        var cast = new VoiceCast();
        Assert.Equal(VoiceGender.Male, cast.Narrator.Gender());
        var voice = cast.VoiceFor(new SpokenLine("", "", Gender: VoiceGender.Female), out var assigned);
        Assert.Equal(cast.Partner, voice);
        Assert.False(assigned);
        Assert.Equal(cast.Narrator, cast.VoiceFor(new SpokenLine("", ""), out _));
    }

    [Fact]
    public void ManualChoiceIsSaved()
    {
        var cast = new VoiceCast();
        cast.Assign(DubbingVoice.Female3, "Mimir");
        var restored = JsonSerializer.Deserialize<VoiceCast>(JsonSerializer.Serialize(cast))!;
        Assert.Equal(DubbingVoice.Female3, restored.VoiceFor(new SpokenLine("", "", "MIMIR"), out _));
        Assert.Contains("\"Female3\"", JsonSerializer.Serialize(cast));
    }
}

public class SpeechAudioTests
{
    private static float[] Tone(double seconds) =>
        Enumerable.Range(0, (int)(seconds * 24000)).Select(i => MathF.Sin(i * 0.1f) * 0.5f).ToArray();

    private static float[] Silence(double seconds) => new float[(int)(seconds * 24000)];

    [Fact]
    public void LongPausesAreShortenedTo200Ms()
    {
        var audio = new SpeechAudio([.. Silence(0.2), .. Tone(0.3), .. Silence(0.4), .. Tone(0.3), .. Silence(0.5)], 24000);
        Assert.Equal(0.91, audio.Tightened().Duration, 0.03);
    }

    [Fact]
    public void ShortPausesStay()
    {
        var audio = new SpeechAudio([.. Tone(0.3), .. Silence(0.1), .. Tone(0.3)], 24000);
        Assert.Equal(audio.Duration, audio.Tightened(lead: 0, tail: 0).Duration, 0.02);
    }

    [Fact]
    public void WavRoundTripAndStreamedSize()
    {
        var wav = new SpeechAudio([0f, 0.5f, -0.5f, 0.25f], 24000).ToWav();
        var decoded = SpeechAudio.FromWav(wav);
        Assert.Equal(24000, decoded.SampleRate);
        Assert.Equal(4, decoded.Samples.Length);
        Assert.Equal(0.5f, decoded.Samples[1], 0.001f);
        wav[40] = wav[41] = wav[42] = wav[43] = 0xFF;
        Assert.Equal(4, SpeechAudio.FromWav(wav).Samples.Length);
    }
}

public class DubbingModelTests
{
    [Fact]
    public void LocalConfigPointsAtDownloadedFiles()
    {
        const string original = "weights_path: hf://wite-tech/pocket-tts-turkish/model.safetensors\r\nflow_lm:\r\n  lookup_table:\r\n" +
                                "    tokenizer_path: hf://wite-tech/pocket-tts-turkish/tokenizer.model\r\n";
        var directory = Path.Combine("C:", "Users", "Ali's PC", "dubbing");
        var local = DubbingModel.LocalConfig(original, directory);
        Assert.Contains("weights_path: '" + Path.Combine(directory, "model.safetensors").Replace("'", "''") + "'", local);
        Assert.Contains("    tokenizer_path: '", local);
        Assert.EndsWith("capitalize_first_letter: false\n", local);
    }
}

public class PocketTtsExitTests
{
    [Fact]
    public void ExitMessageShowsTheLastErrorLine()
    {
        var message = PocketTtsServer.ExitMessage(1, new Queue<string>(["loading model", "Error: model.safetensors not found"]));
        Assert.Equal("pocket-tts kapandı (çıkış kodu 1): Error: model.safetensors not found", message);
    }

    [Fact]
    public void ExitMessageWithoutOutputKeepsTheExitCode()
    {
        Assert.Equal("pocket-tts kapandı (çıkış kodu 3)", PocketTtsServer.ExitMessage(3, new Queue<string>()));
    }
}
