// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.Core.Tests;

/// <summary>Hızlı çeviriyi hemen, iyileştirilmiş çeviriyi test bırakınca veren çevirmen.</summary>
internal sealed class FakeProgressive : IProgressiveTranslator
{
    private readonly Dictionary<string, TaskCompletionSource<string?>> refined = [];

    public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult("hızlı: " + text);

    public Task<ProgressiveTranslation> TranslateProgressivelyAsync(string text, CancellationToken cancellationToken = default)
    {
        var better = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (refined) refined[text] = better;
        return Task.FromResult(new ProgressiveTranslation("hızlı: " + text, better.Task));
    }

    public async Task Refine(string text, string? translation)
    {
        while (true)
        {
            lock (refined)
            {
                if (refined.Remove(text, out var better)) { better.SetResult(translation); return; }
            }
            await Task.Delay(5);
        }
    }
}

public class ProgressivePipelineTests
{
    private static readonly Frame Image = Frame.Blank(64, 64);

    private static async Task Run(SubtitlePipeline pipeline, int frames)
    {
        for (var i = 0; i < frames; i++) await pipeline.ProcessAsync(Image, new CropRegion());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition()) { Assert.True(DateTime.UtcNow < deadline); await Task.Delay(5); }
    }

    [Fact]
    public async Task RefinedTranslationReplacesQuickOneWhileLineIsOnScreen()
    {
        var sender = new FakeSender();
        var translator = new FakeProgressive();
        var pipeline = new SubtitlePipeline(new FakeReader("We're gonna call his bluff.", "We're gonna call his bluff."), translator, sender);
        await Run(pipeline, 2);
        await WaitUntil(() => sender.Sent.Count == 1);
        await translator.Refine("We're gonna call his bluff.", "Blöfünü göreceğiz.");
        await pipeline.WaitForTranslationsAsync();
        Assert.Equal(["hızlı: We're gonna call his bluff.", "Blöfünü göreceğiz."], sender.Sent);
        Assert.Equal(("We're gonna call his bluff.", "Blöfünü göreceğiz."), pipeline.LastPair);
    }

    [Fact]
    public async Task FailedRefinementKeepsQuickTranslation()
    {
        var sender = new FakeSender();
        var translator = new FakeProgressive();
        var pipeline = new SubtitlePipeline(new FakeReader("Hold the gate."), translator, sender);
        await Run(pipeline, 1);
        await translator.Refine("Hold the gate.", null);
        await pipeline.WaitForTranslationsAsync();
        Assert.Equal(["hızlı: Hold the gate."], sender.Sent);
    }

    [Fact]
    public async Task RefinementOfOlderLineDoesNotOverwriteNewerLine()
    {
        var sender = new FakeSender();
        var translator = new FakeProgressive();
        var pipeline = new SubtitlePipeline(new FakeReader("Hold the gate.", "Run!"), translator, sender);
        await Run(pipeline, 2);
        await WaitUntil(() => sender.Sent.Count == 2);
        await translator.Refine("Hold the gate.", "Kapıyı tut.");
        await translator.Refine("Run!", "Koş!");
        await pipeline.WaitForTranslationsAsync();
        Assert.Equal(["hızlı: Hold the gate.", "hızlı: Run!", "Koş!"], sender.Sent);
    }

    /// Altyazı titreyip yeniden okununca aynı replik yeniden çevrilmez ve seslendirilmez.
    [Fact]
    public async Task FlickeringLineIsNotTranslatedOrSpokenAgain()
    {
        var sender = new FakeSender();
        var translator = new GatedTranslator();
        translator.Open();
        var spoken = 0;
        var pipeline = new SubtitlePipeline(new FakeReader("Yes, sir.", "", "", "", "", "", "", "Yes, sir."), translator, sender, instant: true);
        pipeline.SpeechReady += _ => spoken++;
        for (var i = 0; i < 8; i++) { await pipeline.ProcessAsync(Image, new CropRegion()); await pipeline.WaitForTranslationsAsync(); }
        Assert.Equal(["Yes, sir."], translator.Requested);
        Assert.Equal(["TR: Yes, sir.", "TR: Yes, sir."], sender.Sent);
        Assert.Equal(1, spoken);
    }

    /// Konuşmacı adı yazıdan sonra belirince ("Moon Here... take my radio.") aynı replik yeniden seslendirilmez.
    [Fact]
    public async Task SpeakerNameAppearingLaterIsSameLine()
    {
        Assert.True(SubtitlePipeline.SameLine(TextMatch.Normalize("Here... take my radio."), TextMatch.Normalize("Moon Here... take my radio.")));
        Assert.True(SubtitlePipeline.SameLine(TextMatch.Normalize("Cho [Shit...]"), TextMatch.Normalize("Cho [Shit...J")));
        Assert.False(SubtitlePipeline.SameLine(TextMatch.Normalize("Get ready to shoot."), TextMatch.Normalize("Soldiers are down there.")));
        await Task.CompletedTask;
    }

    /// Anlık mod: yeni satır gelir gelmez eski çeviri kalkar.
    [Fact]
    public async Task InstantModeClearsPreviousLineAsSoonAsNewOneAppears()
    {
        var sender = new FakeSender();
        var translator = new GatedTranslator();
        var pipeline = new SubtitlePipeline(new FakeReader("Hold the gate.", "Run!"), translator, sender, instant: true);
        await Run(pipeline, 1);
        await translator.Release("Hold the gate.");
        await WaitUntil(() => sender.Sent.Count == 1);
        Assert.Equal(0, sender.Clears);
        await Run(pipeline, 1);
        // "Run!" henüz çevrilmeden "Hold the gate." TV'den kalkmış olmalı.
        Assert.Equal(1, sender.Clears);
        await translator.Release("Run!");
        await pipeline.WaitForTranslationsAsync();
        Assert.Equal(["TR: Hold the gate.", "TR: Run!"], sender.Sent);
    }

    /// Anlık mod: yerini yenisine bırakmış satırın geç gelen çevirisi gösterilmez.
    [Fact]
    public async Task InstantModeDropsTranslationOfLineThatWasReplaced()
    {
        var sender = new FakeSender();
        var translator = new GatedTranslator();
        var pipeline = new SubtitlePipeline(new FakeReader("Hold the gate.", "Run!"), translator, sender, instant: true);
        await Run(pipeline, 2);
        await translator.Release("Hold the gate.");
        await translator.Release("Run!");
        await pipeline.WaitForTranslationsAsync();
        Assert.Equal(["TR: Run!"], sender.Sent);
    }

    /// Anlık mod: ekrandan kalkmış satırın geç gelen çevirisi gösterilmez.
    [Fact]
    public async Task InstantModeDropsTranslationArrivingAfterLineLeft()
    {
        var sender = new FakeSender();
        var translator = new GatedTranslator();
        var pipeline = new SubtitlePipeline(new FakeReader("Hold the gate.", "", "", "", "", "", ""), translator, sender, instant: true);
        await Run(pipeline, 7);
        Assert.Equal(1, sender.Clears);
        translator.Open();
        await pipeline.WaitForTranslationsAsync();
        Assert.Empty(sender.Sent);
    }

    /// Anlık mod: iyileştirilmiş çeviri yalnız satır hâlâ oyundaysa yazılır.
    [Fact]
    public async Task InstantModeDropsRefinementAfterLineLeft()
    {
        var sender = new FakeSender();
        var translator = new FakeProgressive();
        var pipeline = new SubtitlePipeline(new FakeReader("Hold the gate.", "", "", "", "", "", ""), translator, sender, instant: true);
        await Run(pipeline, 1);
        await WaitUntil(() => sender.Sent.Count == 1);
        await Run(pipeline, 6);
        await translator.Refine("Hold the gate.", "Kapıyı tut.");
        await pipeline.WaitForTranslationsAsync();
        Assert.Equal(["hızlı: Hold the gate."], sender.Sent);
    }
}
