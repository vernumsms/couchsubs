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
}
