// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class GameSpeechTests
{
    private static float[] Tone(double seconds, float amplitude, int rate = 48000) =>
        [.. Enumerable.Range(0, (int)(seconds * rate)).Select(i => amplitude * (float)Math.Sin(i * 2 * Math.PI * 220 / rate))];

    [Fact]
    public void SpeechBetweenSilencesBecomesOneSegment()
    {
        var segments = new List<SpeechSegment>();
        var segmenter = new SpeechSegmenter(segments.Add, () => 10_000);
        segmenter.Feed(Tone(1.0, 0.001f), 48000);
        segmenter.Feed(Tone(1.5, 0.3f), 48000);
        segmenter.Feed(Tone(1.0, 0.001f), 48000);

        var segment = Assert.Single(segments);
        // 1,5 sn konuşma + başta en çok 0,3 sn + sonda yarım saniyelik sessizlik, 16 kHz'de.
        Assert.InRange(segment.Samples.Length, (int)(1.5 * 16000), (int)(2.4 * 16000));
    }

    [Fact]
    public void ShortNoiseIsNotSpeech()
    {
        var segments = new List<SpeechSegment>();
        var segmenter = new SpeechSegmenter(segments.Add);
        segmenter.Feed(Tone(1.0, 0.001f), 48000);
        segmenter.Feed(Tone(0.15, 0.4f), 48000);
        segmenter.Feed(Tone(1.0, 0.001f), 48000);
        Assert.Empty(segments);
    }

    [Fact]
    public void LongSpeechIsCutSoDelayDoesNotBuildUp()
    {
        var segments = new List<SpeechSegment>();
        var segmenter = new SpeechSegmenter(segments.Add);
        segmenter.Feed(Tone(20, 0.3f), 48000);
        // Hiç değişmeyen ses (müzik gibi) bir süre sonra arka plan sayılır; parça yine de 8 sn'yi geçmez.
        Assert.NotEmpty(segments);
        Assert.All(segments, segment => Assert.True(segment.Samples.Length <= 8 * 16000 + 480));
    }

    [Theory]
    [InlineData("[Music]", null)]
    [InlineData(" Thank you.", null)]
    [InlineData("(explosion) Get down!", "Get down!")]
    [InlineData("We need to move, now.", "We need to move, now.")]
    public void RecognizerFillersAreDropped(string text, string? expected) =>
        Assert.Equal(expected, SpeechSegmenter.Clean(text));

    [Fact]
    public async Task HeardSpeechIsShownWhenTheGameHasNoSubtitle()
    {
        var sender = new FakeSender();
        var pipeline = new SubtitlePipeline(new FakeReader(), new FakeTranslator(), sender);
        await pipeline.ShowHeardAsync("Get down!", Environment.TickCount64);
        Assert.Equal(["TR: Get down!"], sender.Sent);
    }

    [Fact]
    public async Task HeardSpeechIsSkippedWhenTheGameShowedItsSubtitle()
    {
        var sender = new FakeSender();
        var pipeline = new SubtitlePipeline(new FakeReader("Get down!", "Get down!", "", "", "", "", "", "", ""),
            new FakeTranslator(), sender);
        var spokeAt = Environment.TickCount64;
        for (var i = 0; i < 9; i++)
        {
            await pipeline.ProcessAsync(Frame.Blank(64, 64), new CropRegion());
            await pipeline.WaitForTranslationsAsync();
        }
        await pipeline.ShowHeardAsync("Get down!", spokeAt);
        Assert.Equal(["TR: Get down!"], sender.Sent);
    }
}
