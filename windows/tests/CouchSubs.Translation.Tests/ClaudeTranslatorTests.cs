// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;
using CouchSubs.Core.Dubbing;
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

        public List<string> Schemas { get; } = [];

        public Task<string> CompleteJsonAsync(string system, string user, string schema, CancellationToken cancellationToken)
        {
            lock (Requests) { Requests.Add((system, user)); Schemas.Add(schema); }
            return Task.FromResult(reply(user));
        }
    }

    /// <summary>Yapılandırılmış (JSON) isteği reddeden, düz isteği yanıtlayan Claude.</summary>
    private sealed class NoJsonClaude(string reply) : IClaudeCompletion
    {
        public int JsonCalls { get; private set; }

        public Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken) => Task.FromResult(reply);

        public Task<string> CompleteJsonAsync(string system, string user, string schema, CancellationToken cancellationToken)
        {
            JsonCalls++;
            throw new ClaudeException(OnlineKeyState.Invalid, "output_config.format is not supported");
        }
    }

    [Fact]
    public async Task RejectedJsonRequestFallsBackToPlainTranslation()
    {
        var claude = new NoJsonClaude("Koş!");
        var translator = new ClaudeTranslator(claude, new FakeLocal(), dubbing: () => true);

        Assert.Equal("Koş!", await (await translator.TranslateProgressivelyAsync("Run!")).Refined);
        // Sonraki satırlarda JSON yeniden denenmez; düz çeviri sürer.
        Assert.Equal("Koş!", await (await translator.TranslateProgressivelyAsync("Stop!")).Refined);
        Assert.Equal(1, claude.JsonCalls);
        Assert.Equal("Koş!", translator.SpokenLines("Run!")[0].Turkish);
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

    [Fact]
    public async Task DubbingAsksForSpokenTextToneAndGender()
    {
        const string json = """
            {"lines":[
              {"text":"MOON: Blöfünü göreceğiz.","spoken":"Blöfünü göreceğiz.","emotion":"angry","gender":"female"},
              {"text":"Peki ya yanılıyorsan?","spoken":"Ya yanılıyorsan?","emotion":"surprised","gender":"unknown"}
            ]}
            """;
        var claude = new FakeClaude(_ => json);
        var translator = new ClaudeTranslator(claude, new FakeLocal(), dubbing: () => true);
        var source = Lines("MOON: We're gonna call his bluff.", "And if you're wrong?");

        var result = await translator.TranslateProgressivelyAsync(source);

        Assert.Equal(Lines("MOON: Blöfünü göreceğiz.", "Peki ya yanılıyorsan?"), await result.Refined);
        Assert.Contains("Voice-over is on", claude.Requests[0].System);
        Assert.Equal(ClaudeTranslator.DubbingSchema, claude.Schemas[0]);
        var lines = translator.SpokenLines(source);
        Assert.Equal(2, lines.Count);
        Assert.Equal(("MOON", "Blöfünü göreceğiz.", (SpeechEmotion?)SpeechEmotion.Angry, (VoiceGender?)VoiceGender.Female),
            (lines[0].Speaker, lines[0].Turkish, lines[0].Emotion, lines[0].Gender));
        Assert.Equal(("Ya yanılıyorsan?", (SpeechEmotion?)SpeechEmotion.Surprised, (VoiceGender?)null),
            (lines[1].Turkish, lines[1].Emotion, lines[1].Gender));
    }

    [Fact]
    public async Task DubbingReplyWithWrongLineCountIsNotUsed()
    {
        const string json = """{"lines":[{"text":"Kapıyı tut ve koş!","spoken":"Kapıyı tut ve koş!","emotion":"neutral","gender":"unknown"}]}""";
        var translator = new ClaudeTranslator(new FakeClaude(_ => json), new FakeLocal(), dubbing: () => true);
        Assert.Null(await (await translator.TranslateProgressivelyAsync(Lines("Hold the gate.", "Run!"))).Refined);
    }

    /// Call of Duty: ad iki noktasız ("Lt. West Okay…"); Claude adı ayırır, arayüz yazısı seslendirilmez.
    [Fact]
    public async Task SpeakerWithoutColonAndInterfaceTextAreHandled()
    {
        const string json = """
            {"lines":[
              {"speaker":"Lt. West","text":"Tamam, dinleyin.","spoken":"Tamam, dinleyin.","emotion":"calm","gender":"male","dialogue":true},
              {"speaker":"","text":"Han 86","spoken":"Han seksen altı","emotion":"neutral","gender":"unknown","dialogue":false}
            ]}
            """;
        var translator = new ClaudeTranslator(new FakeClaude(_ => json), new FakeLocal(), dubbing: () => true);
        var source = Lines("Lt. West Okay, listen up.", "Han 86");

        Assert.Equal(Lines("Lt. West: Tamam, dinleyin.", "Han 86"), await (await translator.TranslateProgressivelyAsync(source)).Refined);
        var lines = translator.SpokenLines(source);
        Assert.Single(lines);
        Assert.Equal(("Lt. West", "Tamam, dinleyin."), (lines[0].Speaker, lines[0].Turkish));
    }

    [Fact]
    public void DubbingSchemaIsValidJson()
    {
        using var document = System.Text.Json.JsonDocument.Parse(ClaudeTranslator.DubbingSchema);
        Assert.Equal("object", document.RootElement.GetProperty("type").GetString());
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
