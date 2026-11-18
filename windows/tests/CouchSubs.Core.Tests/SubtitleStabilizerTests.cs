// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

public class SubtitleStabilizerTests
{
    /// Noktalamayla bitmeyen satır yazılmakta olabilir; iki kare aynı okununca gönderilir.
    [Fact]
    public void UnfinishedLineIsSentAfterTwoStableFrames()
    {
        var s = new SubtitleStabilizer();
        Assert.Equal(StabilizerAction.None, s.Feed("We do not have time"));
        Assert.Equal(StabilizerAction.Translate("We do not have time"), s.Feed("We do not have time"));
        Assert.Equal(StabilizerAction.None, s.Feed("We do not have time"));
    }

    /// Süslü yazıda aynı cümle her karede başka çöple okunur; TV'de iki çeviri olarak görünmemeli.
    [Fact]
    public void GarbledRereadOfSentLineDoesNotReemit()
    {
        var s = new SubtitleStabilizer();
        const string first = "Across the deep waters, Pbo went,\nhoping a boat would carry him home.";
        Assert.Equal(StabilizerAction.Translate(first), s.Feed(first));
        Assert.Equal(StabilizerAction.None, s.Feed("Across the deep waters O went,"));
        Assert.Equal(StabilizerAction.None, s.Feed("Across the deep waters O went,"));
        Assert.Equal(StabilizerAction.Translate("We should keep moving."), s.Feed("We should keep moving."));
    }

    /// Cümle bitmişse beklemeye gerek yok; hızlı konuşmada her kare önemli.
    [Theory]
    [InlineData("We do not have time.")]
    [InlineData("Wait!")]
    [InlineData("Who are you?")]
    [InlineData("I know…")]
    [InlineData("\"Run.\"")]
    public void FinishedSentenceIsSentOnFirstFrame(string line)
    {
        var s = new SubtitleStabilizer();
        Assert.Equal(StabilizerAction.Translate(line), s.Feed(line));
        Assert.Equal(StabilizerAction.None, s.Feed(line));
    }

    [Fact]
    public void OcrNoiseDoesNotReemit()
    {
        var s = new SubtitleStabilizer();
        s.Feed("The gate will not hold"); s.Feed("The gate will not hold");
        Assert.Equal(StabilizerAction.None, s.Feed("The gale will not hold"));
        Assert.Equal(StabilizerAction.None, s.Feed("The gale will not hold"));
    }

    [Fact]
    public void FlickeringHudDoesNotSpam()
    {
        var s = new SubtitleStabilizer();
        var sent = 0;
        for (var i = 0; i < 20; i++)
            if (s.Feed(i % 2 == 0 ? "24 / 120" : "").Kind == StabilizerKind.Translate) sent++;
        Assert.Equal(0, sent);
    }

    [Fact]
    public void TypewriterSendsCompletedLine()
    {
        var s = new SubtitleStabilizer();
        s.Feed("We do"); s.Feed("We do not have");
        Assert.Equal(StabilizerAction.Translate("We do not have time for this, boy."), s.Feed("We do not have time for this, boy."));
        Assert.Equal(StabilizerAction.None, s.Feed("We do not have time for this, boy."));
    }

    [Fact]
    public void TwoEmptyFramesClearAfterSend()
    {
        var s = new SubtitleStabilizer();
        s.Feed("Hello there"); s.Feed("Hello there");
        Assert.Equal(StabilizerAction.None, s.Feed(""));
        Assert.Equal(StabilizerAction.Clear, s.Feed(""));
    }

    [Fact]
    public void AlreadyTranslatedIsSkipped()
    {
        var s = new SubtitleStabilizer();
        Assert.False(s.ShouldSend("Kullanıcı Ekle Oturum Açtı", "Kullanıcı Ekle Oturum Açtı"));
        Assert.True(s.ShouldSend("Add user", "Kullanıcı ekle"));
    }

    [Fact]
    public void ResetClearsOnlyAfterSomethingWasSent()
    {
        var s = new SubtitleStabilizer();
        Assert.Equal(StabilizerAction.None, s.Reset());
        s.Feed("Hello there"); s.Feed("Hello there");
        Assert.Equal(StabilizerAction.Clear, s.Reset());
        Assert.Equal(StabilizerAction.None, s.Reset());
    }

    /// Çevirisi başarısız olan satır ekranda kaldıkça yeniden denenebilmeli.
    [Fact]
    public void ForgottenLineIsTranslatedAgain()
    {
        var s = new SubtitleStabilizer();
        s.Feed("Hold the gate");
        Assert.Equal(StabilizerAction.Translate("Hold the gate"), s.Feed("Hold the gate"));
        s.ForgetSent();
        Assert.Equal(StabilizerAction.None, s.Feed("Hold the gate"));
        Assert.Equal(StabilizerAction.Translate("Hold the gate"), s.Feed("Hold the gate"));
    }

    /// Anlık modda tam cümle ilk karede "erken" bildirilir (çevirisi başlayabilir), gönderim yine ikinci karede.
    [Fact]
    public void FinishedSentenceIsOfferedEarlyBeforeConfirmation()
    {
        var s = new SubtitleStabilizer(fastSentences: false);

        Assert.Equal(StabilizerAction.None, s.Feed("Drive! Go, go, go!"));
        Assert.Equal("Drive! Go, go, go!", s.Early);
        Assert.Equal(StabilizerAction.Translate("Drive! Go, go, go!"), s.Feed("Drive! Go, go, go!"));
        Assert.Null(s.Early);
        // Gönderilmiş satırın yeniden okuması ve yarım satır erken bildirilmez.
        Assert.Equal(StabilizerAction.None, s.Feed("Drive! Go, go, go!"));
        Assert.Null(s.Early);
        s.Feed("");
        s.Feed("Keep your head");
        Assert.Null(s.Early);
    }
}
