// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core.Dubbing;
using Xunit;

namespace CouchSubs.Core.Tests;

public class ProsodyTests
{
    /// <summary>Perdesi zamanla <paramref name="swing"/> yarım ton salınan, hece gibi aralıklı ses.</summary>
    private static float[] Voice(double hz, double seconds, double swing = 0, float level = 0.3f, int rate = 16000)
    {
        var samples = new float[(int)(seconds * rate)];
        double phase = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)rate;
            var pitch = hz * Math.Pow(2, swing * Math.Sin(2 * Math.PI * 1.5 * t) / 12);
            phase += 2 * Math.PI * pitch / rate;
            var syllable = Math.Sin(2 * Math.PI * 3 * t) > -0.6 ? 1.0 : 0.1;
            // Perde ve iki üst katı: ses telleri gibi zengin dalga.
            samples[i] = (float)(level * syllable * (Math.Sin(phase) + 0.5 * Math.Sin(2 * phase) + 0.25 * Math.Sin(3 * phase)));
        }
        return samples;
    }

    [Theory]
    [InlineData(110)]
    [InlineData(220)]
    [InlineData(300)]
    public void FindsThePitch(double hz)
    {
        var measured = Prosody.Measure(Voice(hz, 2));
        Assert.True(measured.HasPitch);
        Assert.InRange(Prosody.Semitones(measured.Pitch, hz), 0, 0.5);
    }

    [Fact]
    public void LivelySpeechHasMoreSpreadAndLouderSpeechMoreLoudness()
    {
        var flat = Prosody.Measure(Voice(150, 2));
        var lively = Prosody.Measure(Voice(150, 2, swing: 4, level: 0.6f));
        Assert.True(lively.Spread > flat.Spread + 1);
        Assert.True(lively.Loudness > flat.Loudness + 4);
    }

    [Fact]
    public void MeasuresAudioAtAnyRate()
    {
        var measured = Prosody.Measure(new SpeechAudio(Voice(180, 2, rate: 24000), 24000));
        Assert.InRange(Prosody.Semitones(measured.Pitch, 180), 0, 0.5);
    }

    [Fact]
    public void PromptRejectsSpeechThatDriftedToAnotherVoice()
    {
        var prompt = new VoicePrompt("/voices/klon/c2-normal-v1.wav", [], "#2", Pitch: 118);
        Assert.True(prompt.Matches(new SpeechAudio(Voice(125, 2), 16000)));
        // Denemede kısa bir kesitten yapılan örnek, 118 Hz'lik erkek sesini 222 Hz okudu.
        Assert.False(prompt.Matches(new SpeechAudio(Voice(222, 2), 16000)));
    }

    [Fact]
    public void LineMoodComesFromTheTranslatorWhenItChoseOne()
    {
        Assert.True(CharacterVoices.LineArousal(new SpokenLine("Get down!", "Yere yat!", Intensity: SpeechIntensity.Loud, Emotion: SpeechEmotion.Angry))
                    >= CharacterVoices.MoodThreshold);
        Assert.True(CharacterVoices.LineArousal(new SpokenLine("I know.", "Biliyorum.", Intensity: SpeechIntensity.Quiet, Emotion: SpeechEmotion.Sad))
                    <= -CharacterVoices.MoodThreshold);
        Assert.Equal(0, CharacterVoices.LineArousal(new SpokenLine("Okay.", "Tamam.", Intensity: SpeechIntensity.Normal)));
        // Yapay zekâ seçmediyse İngilizcedeki ipuçları.
        Assert.True(CharacterVoices.LineArousal(new SpokenLine("GET DOWN! NOW!", "Yere yat!")) >= CharacterVoices.MoodThreshold);
        Assert.True(CharacterVoices.LineArousal(new SpokenLine("I... I don't know.", "Ben... bilmiyorum.")) < 0);
    }
}
