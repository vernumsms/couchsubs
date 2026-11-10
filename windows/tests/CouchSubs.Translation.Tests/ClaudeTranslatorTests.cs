// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;
using CouchSubs.Core.Dubbing;
using CouchSubs.Translation;
using Xunit;

namespace CouchSubs.Translation.Tests;

public class ClaudeTranslatorTests
{
    /// <summary>Düz satırlı test yanıtını yapılandırılmış (JSON) yanıta çevirir; zaten JSON ise dokunmaz.</summary>
    private static string AsJson(string plain) =>
        plain.TrimStart().StartsWith('{') ? plain
        : System.Text.Json.JsonSerializer.Serialize(new
        {
            lines = plain.ReplaceLineEndings("\n").Split('\n').Select(line => new
            {
                speaker = "", text = line, spoken = "", emotion = "neutral", gender = "unknown", dialogue = true, background = false,
            }),
        });

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
            return Task.FromResult(AsJson(reply(user)));
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
        Assert.StartsWith(ClaudeTranslator.SystemPrompt, claude.Requests[0].System);
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
        Assert.Contains("[tr] MOON: Stay close.", second);
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
        // Yapılandırılmış istek reddedilince düz istek de denenir; ikisi de reddedilince Claude'a ara verilir.
        Assert.Equal(2, calls);
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
    public async Task ReplyWithMergedLinesIsJoinedIntoOneSentence()
    {
        var translator = new ClaudeTranslator(new FakeClaude(_ => "Kapıyı tut ve koş!"), new FakeLocal());
        var result = await translator.TranslateProgressivelyAsync(Lines("Hold the gate.", "Run!"));
        Assert.Equal("Kapıyı tut ve koş!", await result.Refined);
    }

    [Fact]
    public async Task TranslationsAreRememberedPerGameAcrossSessions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "couchsubs-hafiza-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new FakeClaude(_ => "Kapıyı tut.");
            var memory = new TranslationMemory(folder);
            var translator = new ClaudeTranslator(first, new FakeLocal(), memory: memory, game: () => ("Call of Duty", ""));
            await (await translator.TranslateProgressivelyAsync("Hold the gate.")).Refined;
            memory.Flush();
            Assert.True(File.Exists(Path.Combine(folder, "call-of-duty.json")));

            // Yeni oturum (uygulama yeniden açıldı): satır Claude'a gitmeden, hemen gelir.
            var second = new FakeClaude(_ => "YANLIŞ");
            var again = new ClaudeTranslator(second, new FakeLocal(), memory: new TranslationMemory(folder), game: () => ("Call of Duty", ""));
            Assert.Equal("Kapıyı tut.", (await again.TranslateProgressivelyAsync("Hold the gate.")).Quick);
            Assert.Empty(second.Requests);

            // Başka oyunun hafızası ayrıdır.
            var other = new ClaudeTranslator(second, new FakeLocal(), memory: new TranslationMemory(folder), game: () => ("God of War", ""));
            await (await other.TranslateProgressivelyAsync("Hold the gate.")).Refined;
            Assert.Single(second.Requests);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task GameNameAndPlayerNotesAreSentToClaude()
    {
        var claude = new FakeClaude(_ => "Koş!");
        var translator = new ClaudeTranslator(claude, new FakeLocal(), game: () => ("Call of Duty", "Ghost = çevirme"));
        await (await translator.TranslateProgressivelyAsync("Run!")).Refined;
        Assert.Contains("Game: Call of Duty", claude.Requests[0].User);
        Assert.Contains("Ghost = çevirme", claude.Requests[0].User);
    }

    [Fact]
    public void GameNameBecomesSafeFileName()
    {
        Assert.Equal("call-of-duty-black-ops-7", TranslationMemory.FileName("Call of Duty: Black Ops 7"));
        Assert.Equal("genel", TranslationMemory.FileName("  "));
    }

    [Fact]
    public async Task ExplanationInsteadOfTranslationIsNotShown()
    {
        var translator = new ClaudeTranslator(new FakeClaude(_ =>
            "I can't make out this line reliably; the OCR text is too garbled, so I'm leaving it empty."), new FakeLocal());
        var result = await translator.TranslateProgressivelyAsync("fie here sonlcwllt:re. ]");
        Assert.True(string.IsNullOrEmpty(await result.Refined));
        Assert.False(ClaudeTranslator.IsComment("Price: Buraya gel!"));
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

    /// Claude yarım kalan iki satırı tek cümleye birleştirdiyse de çeviri kullanılır (eskiden atılıyordu).
    [Fact]
    public async Task DubbingReplyWithMergedLinesIsStillUsed()
    {
        const string json = """{"lines":[{"speaker":"News Reporter","text":"Tanıklar ağır topçu ateşi ve hasar bildiriyor.","spoken":"","emotion":"neutral","gender":"unknown","dialogue":true}]}""";
        var translator = new ClaudeTranslator(new FakeClaude(_ => json), new FakeLocal(), dubbing: () => true);
        var source = Lines("News Reporter: Witnesses are reporting heavy artillery fire", "News Reporter: and significant damage.");

        Assert.Equal("News Reporter: Tanıklar ağır topçu ateşi ve hasar bildiriyor.", await (await translator.TranslateProgressivelyAsync(source)).Refined);
        var line = Assert.Single(translator.SpokenLines(source));
        Assert.Equal(("News Reporter", "Tanıklar ağır topçu ateşi ve hasar bildiriyor."), (line.Speaker, line.Turkish));
    }

    /// Kumanda ipucu ("MOUNT") konuşmanın yanında altyazıda gösterilmez; ekranda yalnız arayüz yazısı varsa gösterilir.
    [Fact]
    public async Task InterfaceTextIsHiddenNextToDialogue()
    {
        const string both = """{"lines":[{"speaker":"","text":"DAĞ","spoken":"","emotion":"neutral","gender":"unknown","dialogue":false},{"speaker":"Jae","text":"Hadi, itin!","spoken":"","emotion":"angry","gender":"unknown","dialogue":true}]}""";
        var translator = new ClaudeTranslator(new FakeClaude(_ => both), new FakeLocal(), dubbing: () => true);
        Assert.Equal("Jae: Hadi, itin!", await (await translator.TranslateProgressivelyAsync(Lines("MOUNT", "Jae C'mon, push!"))).Refined);

        // Ekranda yalnız arayüz yazısı varsa ("ARMOR PLATE / FULL") altyazı boştur: gösterilmez.
        const string only = """{"lines":[{"speaker":"","text":"ZIRH PLAKASI","spoken":"","emotion":"neutral","gender":"unknown","dialogue":false}]}""";
        var gauge = new ClaudeTranslator(new FakeClaude(_ => only), new FakeLocal(), dubbing: () => true);
        Assert.Equal("", await (await gauge.TranslateProgressivelyAsync("ARMOR PLATE")).Refined);
        Assert.Empty(gauge.SpokenLines("ARMOR PLATE"));
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

        // Silah adı (arayüz yazısı) konuşmanın yanında gösterilmez ve seslendirilmez.
        Assert.Equal("Lt. West: Tamam, dinleyin.", await (await translator.TranslateProgressivelyAsync(source)).Refined);
        var lines = translator.SpokenLines(source);
        Assert.Single(lines);
        Assert.Equal(("Lt. West", "Tamam, dinleyin."), (lines[0].Speaker, lines[0].Turkish));
    }

    /// Arka plandaki konuşma işaretlenir; ekranda tek konuşma kaldıysa o arka plan sayılmaz.
    [Fact]
    public async Task BackgroundLinesAreMarked()
    {
        const string two = """
            {"lines":[
              {"speaker":"Konni Guard","text":"Helikoptere gidiyor!","spoken":"","emotion":"angry","gender":"unknown","dialogue":true,"background":true},
              {"speaker":"Price","text":"Makarov nerede?","spoken":"","emotion":"angry","gender":"male","dialogue":true,"background":false}
            ]}
            """;
        var translator = new ClaudeTranslator(new FakeClaude(_ => two), new FakeLocal(), dubbing: () => true);
        var source = Lines("Konni Guard [He's heading to the helo!]", "Price Where is Makarov?");
        await (await translator.TranslateProgressivelyAsync(source)).Refined;
        Assert.Equal([true, false], translator.SpokenLines(source).Select(line => line.Background));

        const string one = """{"lines":[{"speaker":"","text":"Helikoptere gidiyor!","spoken":"","emotion":"angry","gender":"unknown","dialogue":true,"background":true}]}""";
        var alone = new ClaudeTranslator(new FakeClaude(_ => one), new FakeLocal(), dubbing: () => true);
        await (await alone.TranslateProgressivelyAsync("[He's heading to the helo!]")).Refined;
        Assert.False(Assert.Single(alone.SpokenLines("[He's heading to the helo!]")).Background);
    }

    /// Oyun önceki satırı ekranda tutup yenisini altına ekleyince ("A / B", sonra "B / C") Claude'a yalnız C gider.
    [Fact]
    public async Task OnlyNewLinesAreSentToClaude()
    {
        var claude = new FakeClaude(user =>
        {
            var lines = LastLine(user.ReplaceLineEndings("\n")).Split('\n');
            return string.Join("\n", lines.Select(line => "[tr] " + line));
        });
        var translator = new ClaudeTranslator(claude, new FakeLocal());
        await (await translator.TranslateProgressivelyAsync(Lines("Take out that RPG!", "Drive him back!"))).Refined;

        var second = await (await translator.TranslateProgressivelyAsync(Lines("Drive him back!", "Shoot it down!"))).Refined;

        Assert.EndsWith("Translate:\nShoot it down!", claude.Requests[1].User.ReplaceLineEndings("\n"));
        Assert.Equal(Lines("[tr] Drive him back!", "[tr] Shoot it down!"), second);
        // Hepsi önbellekteyse Claude'a hiç gidilmez; yerel çeviri gösterilmez, kayıtlı Türkçe hemen gelir.
        Assert.Equal(Lines("[tr] Take out that RPG!", "[tr] Shoot it down!"),
            (await translator.TranslateProgressivelyAsync(Lines("Take out that RPG!", "Shoot it down!"))).Quick);
        Assert.Equal(2, claude.Requests.Count);
    }

    /// <summary>İlk isteği test bırakana kadar bekleten Claude.</summary>
    private sealed class SlowClaude : IClaudeCompletion
    {
        public TaskCompletionSource FirstGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Requests { get; } = [];

        public async Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
        {
            int count;
            lock (Requests) { Requests.Add(user); count = Requests.Count; }
            if (count == 1) await FirstGate.Task;
            var lines = LastLine(user.ReplaceLineEndings("\n")).Split('\n');
            return string.Join("\n", lines.Select(line => "[tr] " + line));
        }

        public async Task<string> CompleteJsonAsync(string system, string user, string schema, CancellationToken cancellationToken) =>
            AsJson(await CompleteAsync(system, user, cancellationToken));
    }

    /// Önceki blok hâlâ çevrilirken yenisi gelirse ortak satır yeniden istenmez; ilk isteğin sonucu beklenir.
    [Fact]
    public async Task LineAlreadyBeingTranslatedIsAwaitedNotResent()
    {
        var claude = new SlowClaude();
        var translator = new ClaudeTranslator(claude, new FakeLocal());
        var first = (await translator.TranslateProgressivelyAsync(Lines("Delta's in contact!", "Take out that RPG!"))).Refined;
        var second = (await translator.TranslateProgressivelyAsync(Lines("Take out that RPG!", "We need reinforcements!"))).Refined;
        await Task.Delay(100);
        Assert.Single(claude.Requests);

        claude.FirstGate.SetResult();
        Assert.Equal(Lines("[tr] Delta's in contact!", "[tr] Take out that RPG!"), await first);
        Assert.Equal(Lines("[tr] Take out that RPG!", "[tr] We need reinforcements!"), await second);
        Assert.EndsWith("Translate:\nWe need reinforcements!", claude.Requests[1].ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData("Valeria Luca", "Valeria Luca. They're still on us, we're heading east.", "Valeria")]
    [InlineData("Lt. West", "Lt. West Okay, Sierra-I, listen up.", "Lt. West")]
    [InlineData("Price", "Price Where is Makarov?", "Price")]
    [InlineData("Konni Guard", "Konni Guard [We need the boss out of here!]", "Konni Guard")]
    public void VocativeIsNotPartOfSpeakerName(string speaker, string english, string expected) =>
        Assert.Equal(expected, ClaudeTranslator.WithoutVocative(speaker, english));

    /// "Valeria Luca. They're still on us": konuşan Valeria; Luca hitap. Yanlış ad sonraki repliklere de taşınmaz.
    [Fact]
    public async Task VocativeDoesNotBecomeSpeaker()
    {
        const string json = """{"lines":[{"speaker":"Valeria Luca","text":"Hâlâ peşimizdeler.","spoken":"","emotion":"neutral","gender":"female","dialogue":true,"background":false}]}""";
        var claude = new FakeClaude(_ => json);
        var translator = new ClaudeTranslator(claude, new FakeLocal(), dubbing: () => true);
        var source = "Valeria Luca. They're still on us.";

        Assert.Equal("Valeria: Hâlâ peşimizdeler.", await (await translator.TranslateProgressivelyAsync(source)).Refined);
        Assert.Equal("Valeria", translator.SpokenLines(source)[0].Speaker);
        await (await translator.TranslateProgressivelyAsync("Valeria Exit here.")).Refined;
        Assert.Contains("Names: Valeria", claude.Requests[1].User);
        Assert.DoesNotContain("Valeria Luca", claude.Requests[1].User.Split('\n').Single(line => line.StartsWith("Names:")));
    }

    [Theory]
    [InlineData("here take my radio", "moon here take my radio", true)]
    [InlineData("helo s armored sweetheart american made", "hollis annorod sweetheart american made", true)]
    [InlineData("run", "you need to run", false)]
    [InlineData("get to the wheelhouse", "get out here", false)]
    public void StoredLineMatchingIsStrict(string a, string b, bool same) =>
        Assert.Equal(same, ClaudeTranslator.SameStoredLine(a, b));

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
