// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;
using CouchSubs.Translation;
using Xunit;

namespace CouchSubs.Translation.Tests;

public class ClaudeTranslatorTests
{
    private sealed class FakeClaude(Func<string, string> reply) : IClaudeCompletion
    {
        public List<(string System, string User)> Requests { get; } = [];

        public Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add((system, user));
            return Task.FromResult(reply(user));
        }
    }

    private sealed class FakeLocal : ITextTranslator
    {
        public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult("yerel: " + text);
    }

    private static string LastLine(string user) => user[(user.LastIndexOf("Translate:\n", StringComparison.Ordinal) + "Translate:\n".Length)..];

    [Fact]
    public async Task QuickTranslationIsLocalAndRefinedComesFromClaude()
    {
        var claude = new FakeClaude(user => "\"Blöfünü göreceğiz.\"");
        var translator = new ClaudeTranslator(claude, new FakeLocal());

        var result = await translator.TranslateProgressivelyAsync("We're gonna cali his bluff");

        Assert.Equal("yerel: We're gonna cali his bluff", result.Quick);
        Assert.Equal("Blöfünü göreceğiz.", await result.Refined);
        Assert.Equal(ClaudeTranslator.SystemPrompt, claude.Requests[0].System);
        Assert.Equal("We're gonna cali his bluff", LastLine(claude.Requests[0].User.ReplaceLineEndings("\n")));
    }

    [Fact]
    public async Task EarlierLinesAndSpeakerNamesAreSentAsContext()
    {
        var claude = new FakeClaude(user => "[tr] " + LastLine(user.ReplaceLineEndings("\n")));
        var translator = new ClaudeTranslator(claude, new FakeLocal());

        await (await translator.TranslateProgressivelyAsync("MOON: Stay close.")).Refined;
        await (await translator.TranslateProgressivelyAsync("He's lying.")).Refined;

        var second = claude.Requests[1].User;
        Assert.Contains("EN: MOON: Stay close.", second);
        Assert.Contains("TR: [tr] MOON: Stay close.", second);
        Assert.Contains("Names: MOON", second);
        Assert.EndsWith("He's lying.", second);
    }

    [Fact]
    public async Task RepeatedLineUsesCachedClaudeTranslationImmediately()
    {
        var claude = new FakeClaude(_ => "Kapıyı tut.");
        var translator = new ClaudeTranslator(claude, new FakeLocal());
        await (await translator.TranslateProgressivelyAsync("Hold the gate.")).Refined;

        var again = await translator.TranslateProgressivelyAsync("Hold the gate.");

        Assert.Equal("Kapıyı tut.", again.Quick);
        Assert.Null(await again.Refined);
        Assert.Single(claude.Requests);
    }

    [Fact]
    public async Task FailureKeepsLocalTranslationAndPausesClaude()
    {
        var calls = 0;
        var claude = new FakeClaude(_ => { calls++; throw new ClaudeException(OnlineKeyState.Invalid); });
        var translator = new ClaudeTranslator(claude, new FakeLocal());

        var first = await translator.TranslateProgressivelyAsync("Hold the gate.");
        Assert.Null(await first.Refined);
        Assert.Equal(OnlineKeyState.Invalid, translator.LastError);

        var second = await translator.TranslateProgressivelyAsync("Run!");
        Assert.Equal("yerel: Run!", second.Quick);
        Assert.Null(await second.Refined);
        Assert.Equal(1, calls);
        Assert.Equal("yerel: Run!", await translator.TranslateAsync("Run!"));
    }

    [Fact]
    public async Task RefusedLineDoesNotPauseClaude()
    {
        var claude = new FakeClaude(user => LastLine(user.ReplaceLineEndings("\n")) == "Bad line."
            ? throw new ClaudeException(OnlineKeyState.Unreachable, ClaudeException.Refusal) : "Koş!");
        var translator = new ClaudeTranslator(claude, new FakeLocal());

        Assert.Null(await (await translator.TranslateProgressivelyAsync("Bad line.")).Refined);
        Assert.Equal("Koş!", await (await translator.TranslateProgressivelyAsync("Run!")).Refined);
    }

    [Fact]
    public async Task WithoutLocalModelClaudeTranslationIsShownDirectly()
    {
        var translator = new ClaudeTranslator(new FakeClaude(_ => "Koş!"), fallback: null);
        var result = await translator.TranslateProgressivelyAsync("Run!");
        Assert.Equal("Koş!", result.Quick);
        Assert.Null(await result.Refined);
    }

    [Fact]
    public void ReplyQuotesAreRemoved()
    {
        Assert.Equal("Koş!", ClaudeTranslator.Clean("  “Koş!” \n"));
        Assert.Equal("\"Koş\" dedi.", ClaudeTranslator.Clean("\"Koş\" dedi."));
    }

    /// <summary>Satırları "\n" ile birleştirir (testlerde alt alta altyazı).</summary>
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public async Task StackedLinesKeepTheirOrderAndWrappedSentenceIsJoined()
    {
        var reply = Lines("Kapıyı tut.", "Koş!", "Onu yanlış yöne gönderdik, biliyorsun.");
        var claude = new FakeClaude(_ => reply);
        var translator = new ClaudeTranslator(claude, new FakeLocal());

        var result = await translator.TranslateProgressivelyAsync(Lines("Hold the gate.", "Run!", "We sent him the wrong", "way, you know."));

        Assert.EndsWith(Lines("Translate:", "Hold the gate.", "Run!", "We sent him the wrong way, you know."),
            claude.Requests[0].User.ReplaceLineEndings("\n"));
        Assert.Equal(reply, await result.Refined);
    }

    [Fact]
    public async Task ReplyWithMergedLinesIsNotUsed()
    {
        var translator = new ClaudeTranslator(new FakeClaude(_ => "Kapıyı tut ve koş!"), new FakeLocal());
        var result = await translator.TranslateProgressivelyAsync(Lines("Hold the gate.", "Run!"));
        Assert.Null(await result.Refined);
    }

    [Fact]
    public async Task DubbingKeepsEachSpeaker()
    {
        var source = Lines("MOON: Stay close.", "KRATOS: We move on.");
        var translator = new ClaudeTranslator(new FakeClaude(_ => Lines("MOON: Yakın dur.", "KRATOS: Gidiyoruz.")), new FakeLocal());
        await (await translator.TranslateProgressivelyAsync(source)).Refined;

        var lines = translator.SpokenLines(source);

        Assert.Equal(2, lines.Count);
        Assert.Equal(("MOON", "Yakın dur.", "Stay close."), (lines[0].Speaker, lines[0].Turkish, lines[0].English));
        Assert.Equal(("KRATOS", "Gidiyoruz."), (lines[1].Speaker, lines[1].Turkish));
    }

    [Theory]
    [InlineData("Hold the gate.", "Run!", true)]
    [InlineData("We sent him the wrong", "way, you know.", false)]
    [InlineData("Help him", "Leave him behind", true)]
    [InlineData("I don't know what", "I want anymore", false)]
    [InlineData("Wait for me,", "Atreus", false)]
    [InlineData("This is a much longer line that wraps", "Over to the next one", false)]
    public void LineBreakIsKeptOnlyBetweenSeparateLines(string previous, string line, bool separate) =>
        Assert.Equal(separate, SubtitleSegmenter.StartsNewLine(previous, line));
}
