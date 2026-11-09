// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

/// <summary>Alanda altyazının yanında menü, köşe göstergesi ve kumanda ipucu da okunur (Mac: SubtitleBlockTests).</summary>
public class SubtitleBlockTests
{
    private static readonly TextBox Area = new(0, 0.6, 1, 1);

    private static TextLine Line(string text, double left, double top, double right, double height = 0.07) =>
        new(text, new TextBox(left, top, right, top + height));

    [Fact]
    public void MenuAndCornerTextAreNotSubtitles()
    {
        string[] items = ["MAP", "HELP", "PROGRESS", "PLAYER", "STORY", "ONLINE", "SOCIAL CLUB", "SETTINGS"];
        var menu = items.Select((item, i) => Line(item, 0.05, 0.05 + (i * 0.1), 0.05 + (item.Length * 0.015)));
        var reading = new Reading([.. menu, Line("PHOTO MODE", 0.85, 0.9, 0.97)]);
        Assert.Empty(reading.SubtitleBlock(Area).Lines);
    }

    [Fact]
    public void CenteredSubtitleIsKeptWithoutCornerGauge()
    {
        var reading = new Reading([
            Line("We'll lose these tracks if we don't hurry.", 0.25, 0.7, 0.75),
            Line("9", 0.02, 0.7, 0.04),
            Line("PHOTO MODE", 0.85, 0.9, 0.97),
        ]);
        var block = reading.SubtitleBlock(Area);
        Assert.Equal("We'll lose these tracks if we don't hurry.", block.Text);
        Assert.Equal(new TextBox(0.25, 0.7, 0.75, 0.77), block.Box!.Value with { Bottom = Math.Round(block.Box!.Value.Bottom, 2) });
    }

    /// Aynı anda konuşan iki kişinin satırları arasında boşluk olsa da ikisi de, yukarıdan aşağıya okunur.
    [Fact]
    public void StackedSpeakersWithGapAreBothKept()
    {
        var reading = new Reading([
            Line("Jae Follow me.", 0.4, 0.62, 0.6),
            Line("Park How did they get vehicles here so fast?", 0.3, 0.82, 0.7),
        ]);
        Assert.Equal("Jae Follow me.\nPark How did they get vehicles here so fast?", reading.SubtitleBlock(Area).Text);
    }

    /// Mermi sayacı gibi rakam satırları altyazı değildir.
    [Fact]
    public void AmmoCounterIsDropped()
    {
        var reading = new Reading([Line("31 / 050", 0.8, 0.9, 0.9), Line("Han 86", 0.8, 0.95, 0.9), Line("Copy that.", 0.42, 0.8, 0.58)]);
        Assert.Equal(["Han 86", "Copy that."], reading.WithoutHudNumbers().Lines.Select(line => line.Text));
    }

    [Fact]
    public void TwoLineSubtitleStaysTogether()
    {
        var reading = new Reading([
            Line("Mac and John... they were both shot.", 0.3, 0.6, 0.7),
            Line("We need to move.", 0.4, 0.68, 0.6),
        ]);
        Assert.Equal("Mac and John... they were both shot.\nWe need to move.", reading.SubtitleBlock(Area).Text);
    }

    [Fact]
    public void LeftAlignedSentenceIsStillRead()
    {
        var reading = new Reading([Line("Mafioso: We're worried about this one.", 0.05, 0.8, 0.5)]);
        Assert.Equal("Mafioso: We're worried about this one.", reading.SubtitleBlock(Area).Text);
    }

    [Fact]
    public void LinesWithoutPlaceAreUntouched()
    {
        var reading = Reading.FromText("MAP\nHELP");
        Assert.Same(reading, reading.SubtitleBlock(Area));
    }

    [Theory]
    [InlineData("PHOTO MODE L1 + R1", true)]
    [InlineData("PHOTO MODE [L1] + [R3]", true)]
    [InlineData("Hand me the L1.", false)]
    public void ButtonAtTheEndIsAPrompt(string line, bool prompt) => Assert.Equal(prompt, ButtonPrompts.IsPrompt(line));

    [Theory]
    [InlineData("9? We'll lose these tracks.", "We'll lose these tracks.")]
    [InlineData("100. Mac and John...", "Mac and John...")]
    [InlineData("23m CESARE: It's here! Shoot him!", "CESARE: It's here! Shoot him!")]
    [InlineData("100 men came", "100 men came")]
    public void GaugeNumberStuckToLineIsDropped(string line, string clean) => Assert.Equal(clean, OcrNoise.Clean(line));
}
