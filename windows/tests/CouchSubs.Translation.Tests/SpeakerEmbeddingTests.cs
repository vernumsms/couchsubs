// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core.Dubbing;
using Xunit;
using Xunit.Abstractions;

namespace CouchSubs.Translation.Tests;

/// <summary>
/// Gerçek modelle: COUCHSUBS_SPEAKER_TEST klasöründe voxceleb_resnet34_LM.onnx ve örnek kayıtlar (zira.wav, zira2.wav,
/// david1.wav, david2.wav; aynı kişinin iki farklı cümlesi) varsa çalışır, yoksa geçer.
/// </summary>
public class SpeakerEmbeddingTests(ITestOutputHelper output)
{
    private static string? Folder => Environment.GetEnvironmentVariable("COUCHSUBS_SPEAKER_TEST") is { } folder
                                     && File.Exists(Path.Combine(folder, SpeakerEmbeddingModel.ModelFile)) ? folder : null;

    [Fact]
    public void SameSpeakerIsCloserThanDifferentSpeaker()
    {
        if (Folder is not { } folder) return;
        using var embedder = new SpeakerEmbedder(Path.Combine(folder, SpeakerEmbeddingModel.ModelFile));
        float[] Embed(string name)
        {
            var audio = SpeechAudio.FromWav(File.ReadAllBytes(Path.Combine(folder, name)));
            Assert.Equal(24000, audio.SampleRate);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var embedding = embedder.Embed(CharacterVoices.To16k(audio.Samples))!;
            output.WriteLine($"{name}: {audio.Duration:0.0} sn, {clock.ElapsedMilliseconds} ms");
            return embedding;
        }
        var zira1 = Embed("zira.wav");
        var zira2 = Embed("zira2.wav");
        var david1 = Embed("david1.wav");
        var david2 = Embed("david2.wav");
        double Sim(float[] a, float[] b) => CharacterVoices.Cosine(a, b);
        output.WriteLine($"zira-zira {Sim(zira1, zira2):0.00}, david-david {Sim(david1, david2):0.00}, zira-david (aynı cümle) {Sim(zira1, david1):0.00} / {Sim(zira2, david2):0.00}");
        Assert.True(Sim(zira1, zira2) > CharacterVoices.MatchThreshold);
        Assert.True(Sim(david1, david2) > CharacterVoices.MatchThreshold);
        Assert.True(Sim(zira1, david1) < CharacterVoices.JoinThreshold);
        Assert.True(Sim(zira2, david2) < CharacterVoices.JoinThreshold);

        // Pocket TTS'in bir sesle okuduğu Türkçe cümle de o sese benzemeli (out-zira: zira.wav örneğiyle).
        var cloned = Path.Combine(folder, "out-zira.wav");
        if (File.Exists(cloned))
        {
            var clone = Embed("out-zira.wav");
            output.WriteLine($"klon-zira {Sim(clone, zira2):0.00}, klon-david {Sim(clone, david2):0.00}");
        }
    }
}
