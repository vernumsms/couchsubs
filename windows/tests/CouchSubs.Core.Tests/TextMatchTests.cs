// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class TextMatchTests
{
    [Fact]
    public void NormalizeStripsPunctuationAndCase() =>
        Assert.Equal("kratos we do not have time boy", TextMatch.Normalize("Kratos: We do NOT have time, boy."));

    [Fact]
    public void SimilarToleratesOcrNoise() => Assert.True(TextMatch.IsSimilar("the gate will not hold", "the gale will not hold"));

    [Fact]
    public void DifferentLinesAreNotSimilar() => Assert.False(TextMatch.IsSimilar("we do not have time", "the gate will not hold"));

    [Fact]
    public void EmptyIsNeverSimilarToText() => Assert.False(TextMatch.IsSimilar("", "abc"));

    [Fact]
    public void SameSubtitleIgnoresGarbledWords()
    {
        var sent = TextMatch.Normalize("Across the deep waters, Pbo ou$3e went, hoping a boat would carry him home.");
        var wordSwap = TextMatch.Normalize("Across the deep waters, 2aljd Pbo went, Hoping 3e boat would carry him home now.");
        Assert.False(TextMatch.IsSimilar(wordSwap, sent));
        Assert.True(TextMatch.IsSameSubtitle(wordSwap, sent));
    }

    /// İki satırlık altyazı solarken alt satır kaçırıldı: yeni satır değil. Uzayan metin ise yenidir.
    [Fact]
    public void RereadMissingALineIsSameSubtitle()
    {
        var sent = TextMatch.Normalize("Across the deep waters, Pbo went,\nhoping a boat would carry him home.");
        Assert.True(TextMatch.IsSameSubtitle(TextMatch.Normalize(OcrNoise.Clean("Across the deep waters2aljd O went,")), sent));
        Assert.False(TextMatch.IsSameSubtitle(sent, TextMatch.Normalize("Across the deep waters, Pbo went,")));
    }

    /// Call of Duty'de altyazı solarken okunan bozuk satırlar (kayıttan); Claude bunlardan uydurma cümle çıkarıyordu.
    [Theory]
    [InlineData("[lell now he died. I", "[Tell me how he died.]")]
    [InlineData("Ilhey musl hiding near IIIE crash silp I", "[They must be hiding near the crash site...]")]
    [InlineData("Chow •s still hanqinc; hy a thruat:.", "Chow's still hanging on by a thread.")]
    [InlineData("The largp qray hilildinrg", "Ko The large, gray building.")]
    [InlineData("Palk qonna wanta c.hütk this cur", "Dunn Park- You're gonna wanna check this out.")]
    [InlineData("Kal Hit the hiltton.", "Kal Hit the button.")]
    public void FadingRereadIsSameSubtitle(string reread, string sent) =>
        Assert.True(TextMatch.IsSameSubtitle(TextMatch.Normalize(reread), TextMatch.Normalize(sent)));

    [Theory]
    [InlineData("we do not have time", "the gate will not hold")]
    [InlineData("kal copy yellow triangle", "look for a yellow triangle")]
    [InlineData("fastest route is the north door it may be flooded", "fastest route is the north door")]
    [InlineData("two targets left park", "one target left park")]
    [InlineData("we do not have time for this boy and you know it", "we do not have time")]
    [InlineData("where is the boy", "where is the ship")]
    [InlineData("you should not be here", "i told you to stay with your mother")]
    [InlineData("we have to cross the river before the sun goes down", "the river is too deep for us to cross today")]
    public void SameSubtitleKeepsDistinctLinesApart(string reread, string sent) =>
        Assert.False(TextMatch.IsSameSubtitle(reread, sent));
}
