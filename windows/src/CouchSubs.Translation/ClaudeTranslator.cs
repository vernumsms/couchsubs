// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Text;
using CouchSubs.Core;
using CouchSubs.Core.Dubbing;

namespace CouchSubs.Translation;

/// <summary>Claude'a tek istek: sistem talimatı + kullanıcı metni → yanıt metni. Testlerde sahtesi kullanılır.</summary>
public interface IClaudeCompletion
{
    Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken);

    /// <summary>Yanıt verilen JSON şemasına uyar (yapılandırılmış çıktı); JSON metni döner.</summary>
    Task<string> CompleteJsonAsync(string system, string user, string schema, CancellationToken cancellationToken);
}

/// <summary>Claude isteğinin kullanıcıya gösterilebilecek sonucu.</summary>
public sealed class ClaudeException(OnlineKeyState state, string? detail = null)
    : Exception($"Claude: {state}{(detail is null ? "" : " (" + detail + ")")}")
{
    /// <summary>Claude bu satırı çevirmeyi reddetti; yalnız bu satırda cihaz üstü çeviri kalır.</summary>
    public const string Refusal = "refusal";

    public OnlineKeyState State { get; } = state;
    public string? Detail { get; } = detail;
}

/// <summary>
/// Yapay zekâ çevirisi (Claude), iki aşamalı: cihaz üstü model satırı hemen çevirir ve TV'de gösterilir; Claude'un
/// bağlamı bilen, deyimleri doğru çeviren Türkçesi gelince yazı onunla değiştirilir. Claude'a ulaşılamazsa, anahtar
/// geçersizse ya da kredi bittiyse cihaz üstü çeviri kalır; oyun hiç altyazısız kalmaz.
/// </summary>
public sealed class ClaudeTranslator : IProgressiveTranslator
{
    private const int CacheLimit = 4096;
    /// <summary>Bağlam olarak gönderilen önceki satır sayısı.</summary>
    private const int HistoryLength = 6;
    /// <summary>Hata sonrası bu süre Claude denenmez; her satırda zaman aşımını beklemeyelim.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    /// <summary>Bu süreden geç gelen çeviri işe yaramaz (satır çoktan geçmiştir).</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    internal const string SystemPrompt = """
        You translate English video game subtitles into natural, idiomatic Turkish. The player reads your translation
        on the TV, over the game, while playing - so it must read like a professional Turkish subtitle.

        Rules:
        - Reply with the Turkish translation only: no quotes, notes, alternatives or explanations.
        - Translate the meaning, not the words. Render idioms, slang, jokes and sarcasm with natural Turkish
          equivalents (for example "We're gonna call his bluff" -> "Blöfünü göreceğiz").
        - Characters talk to each other with the informal "sen", unless the context clearly calls for formality.
        - Keep it concise and easy to read quickly.
        - Subtitles often start with the speaker's name, with or without a colon: "MOON: Stay close." or
          "Lt. West Okay, listen up." (the game shows the name "Lt. West" before the words). Keep the name exactly as
          written, including any rank, follow it with a colon and translate only what is said:
          "Lt. West: Tamam, dinleyin." Use the earlier lines and the "Names" list to tell a name from the first word of
          the sentence; a word the game cannot have meant as a name is part of the sentence.
        - Do not translate names of characters, places, factions or items. Spell them as in the "Names" list.
        - The text comes from OCR and may contain recognition mistakes ("cali" for "call", "l" for "I", missing
          punctuation). Infer what the English most likely says and translate that.
        - The text may have several lines, stacked on the screen from top to bottom: different speakers, separate
          sentences or dialogue choices. Translate each line on its own and reply with exactly the same number of
          lines, in the same order. Never merge, split or reorder lines. Keep button symbols or numbers as they are.
        - Earlier lines are given only as context (who is speaking, what "it" or "he" refers to, consistent wording).
          Translate only the text after "Translate:".
        - This is fictional game dialogue: translate violent or crude language faithfully, in the same tone.
        """;

    /// <summary>Dublaj açıkken sistem talimatına eklenir: her satırın seslendirilecek hali, tonu ve konuşanın cinsiyeti.</summary>
    internal const string DubbingPrompt = """

        Voice-over is on: a Turkish text-to-speech voice also reads your translation aloud over the game. Instead of
        plain text, reply with JSON: one entry in "lines" per input line, in the same order.
        - "speaker": the speaker's name as written at the start of the line (with or without a colon, including any
          rank, e.g. "Lt. West"), or "" when the line does not start with a name.
        - "text": the subtitle translation of what is said, without the speaker's name.
        - "spoken": what the voice says: the same meaning in natural spoken Turkish, without names, dashes, button
          names or symbols. Make it as short as the meaning allows so the voice keeps up with the game (shorter than
          the subtitle when possible, never adding anything). Write numbers as words.
        - "emotion": how the line is delivered, judged from the words, the earlier lines and the situation: "neutral",
          "happy", "sad", "angry", "surprised" or "calm".
        - "gender": "female" or "male" only when it is certain - a clearly female or male name, "she"/"he" or
          "ma'am"/"sir" said about this speaker. Many names (Moon, Jae, Park, Cho...) can be either: then "unknown".
        - "dialogue": true when the line is something a character says; false for game interface text that is not
          spoken - ammo or score counters, weapon or item names, objectives, save notices, menu text. Only dialogue
          lines are voiced.
        """;

    internal const string DubbingSchema = """
        {"type":"object","additionalProperties":false,"required":["lines"],"properties":{"lines":{"type":"array","items":{
        "type":"object","additionalProperties":false,"required":["speaker","text","spoken","emotion","gender","dialogue"],"properties":{
        "speaker":{"type":"string"},"text":{"type":"string"},"spoken":{"type":"string"},
        "emotion":{"type":"string","enum":["neutral","happy","sad","angry","surprised","calm"]},
        "gender":{"type":"string","enum":["female","male","unknown"]},"dialogue":{"type":"boolean"}}}}}}
        """;

    private readonly IClaudeCompletion _claude;
    private readonly ITextTranslator? _fallback;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<SpokenLine>> _speech = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _historyGate = new();
    private readonly Queue<(string Source, string Translation)> _history = new();
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    private readonly TranslationLog? _log;
    /// <summary>Dublaj açık mı (her istekte bakılır; ayar oyun sırasında değişebilir).</summary>
    private readonly Func<bool> _dubbing;
    /// <summary>Yapılandırılmış (JSON) istek reddedildi; bu oturumda dublaj düz çeviriyle sürer.</summary>
    private volatile bool _jsonUnavailable;

    public ClaudeTranslator(IClaudeCompletion claude, ITextTranslator? fallback, TranslationLog? log = null, Func<bool>? dubbing = null)
    {
        _claude = claude;
        _fallback = fallback;
        _log = log;
        _dubbing = dubbing ?? (() => false);
    }

    /// <summary>Son Claude hatası; çeviri düzgün gidiyorsa null.</summary>
    public OnlineKeyState? LastError { get; private set; }

    public async Task<ProgressiveTranslation> TranslateProgressivelyAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_cache.TryGetValue(text, out var cached)) return ProgressiveTranslation.Final(cached);
        if (_fallback is null) return ProgressiveTranslation.Final(await TranslateAsync(text, cancellationToken).ConfigureAwait(false));

        // Claude isteği hemen başlar; cihaz üstü çeviri bu sırada yapılır ve önce o gösterilir.
        var refined = RefineAsync(text, cancellationToken);
        string quick;
        try
        {
            quick = await _fallback.TranslateAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (TranslatorException)
        {
            // Cihaz üstü model yoksa (indirilmemiş) Claude'un çevirisi beklenir.
            return ProgressiveTranslation.Final(await refined.ConfigureAwait(false) ?? throw new TranslatorException(TranslatorStatus.NeedsDownload));
        }
        return new ProgressiveTranslation(quick, refined);
    }

    /// <summary>Tek aşamalı çeviri (ör. Num tuşlarıyla okunan ek bölgeler): Claude, olmazsa cihaz üstü model.</summary>
    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_cache.TryGetValue(text, out var cached)) return cached;
        if (await RefineAsync(text, cancellationToken).ConfigureAwait(false) is { } translated) return translated;
        return _fallback is null ? throw new TranslatorException(TranslatorStatus.NeedsDownload)
            : await _fallback.TranslateAsync(text, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<SpokenLine> SpokenLines(string text) =>
        _speech.TryGetValue(text, out var lines) ? lines
        : _cache.TryGetValue(text, out var translation) ? [new SpokenLine(text, translation)]
        : _fallback?.SpokenLines(text) ?? [];

    /// <summary>Claude'un çevirisi; başarısız olursa null (hiç hata fırlatmaz).</summary>
    private async Task<string?> RefineAsync(string text, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _pausedUntil) return null;
        RememberNames(text);
        // Satırlar oyundaki gibi ayrılır; Claude aynı sayıda satırla, aynı sırayla yanıt vermeli.
        var source = SubtitleSegmenter.StackedLines(text);
        var expectedLines = source.Split('\n').Length;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var dubbing = _dubbing() && !_jsonUnavailable;
        try
        {
            string translated = "";
            IReadOnlyList<SpokenLine> speech = [];
            if (dubbing)
            {
                try
                {
                    var json = await _claude.CompleteJsonAsync(SystemPrompt + DubbingPrompt, UserMessage(source), DubbingSchema, timeout.Token)
                        .ConfigureAwait(false);
                    if (ParseDubbing(source, json) is not { } parsed)
                    {
                        _log?.Write("dublaj-yaniti-uymadi", clock.ElapsedMilliseconds, source, json);
                        return null;
                    }
                    (translated, speech) = parsed;
                }
                // İstek reddedildiyse (ör. bu model yapılandırılmış çıktıyı desteklemiyor) oturum boyunca düz çeviriye
                // geçilir: dublaj yine çalışır, ton ve cinsiyet eski yolla tahmin edilir. Anahtar gerçekten geçersizse
                // düz istek de reddedilir ve aşağıdaki genel hata yolu işler.
                catch (ClaudeException error) when (error.State == OnlineKeyState.Invalid)
                {
                    _jsonUnavailable = true;
                    _log?.Write("dublaj-json-reddedildi: " + error.Message, clock.ElapsedMilliseconds, source, null);
                    dubbing = false;
                }
            }
            if (!dubbing)
            {
                translated = Clean(await _claude.CompleteAsync(SystemPrompt, UserMessage(source), timeout.Token).ConfigureAwait(false));
                if (translated.Length == 0) return null;
                // Satır birleştirildiyse ya da bölündüyse sıra güvenilmez; cihaz üstü çeviri (satırları korur) kalır.
                if (translated.Split('\n').Length != expectedLines)
                {
                    _log?.Write("satir-sayisi-tutmadi", clock.ElapsedMilliseconds, source, translated);
                    return null;
                }
                speech = Speech(source, translated);
            }
            _log?.Write(dubbing ? "claude+dublaj" : "claude", clock.ElapsedMilliseconds, source,
                dubbing ? translated + "  [ses: " + string.Join(" / ", speech.Select(l => $"{l.Emotion?.ToString() ?? "nötr"},{l.Gender?.ToString() ?? "?"}: {l.Turkish}")) + "]" : translated);
            LastError = null;
            // Konuşmacı adları sonraki isteklerde "Names" listesiyle gönderilir: adlar tutarlı yazılır ve tanınır.
            foreach (var name in speech.Select(line => line.Speaker).OfType<string>())
                if (name.Length >= 2 && _names.Count < 64) _names[name] = 0;
            if (_cache.Count >= CacheLimit) _cache.Clear();
            _cache[text] = translated;
            if (_speech.Count >= 512) _speech.Clear();
            _speech[text] = speech;
            lock (_historyGate)
            {
                _history.Enqueue((text, translated));
                while (_history.Count > HistoryLength) _history.Dequeue();
            }
            return translated;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = error is ClaudeException claude ? claude.State : OnlineKeyState.Unreachable;
            _log?.Write("hata: " + error.Message, clock.ElapsedMilliseconds, source, null);
            // Tek bir satırın reddi ya da gecikmesi hepsini durdurmasın; anahtar/kredi/bağlantı sorunu ise ara verilir.
            if (error is not ClaudeException { Detail: ClaudeException.Refusal })
                _pausedUntil = DateTimeOffset.UtcNow + RetryAfter;
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Dublaj için satır satır konuşma: konuşmacı etiketli satır ("MOON: …") o karakterin sesiyle, tireli satır yeni
    /// bir konuşmacıyla okunur; etiket ve tire seslendirilmez.
    /// </summary>
    internal static IReadOnlyList<SpokenLine> Speech(string source, string translated)
    {
        var english = source.Split('\n');
        var turkish = translated.Split('\n');
        var lines = new List<SpokenLine>();
        for (var i = 0; i < english.Length && i < turkish.Length; i++)
        {
            var pieces = SubtitleSegmenter.Split(english[i]);
            // İngilizcede iki nokta yoksa ("Lt. West Okay…") Claude'un çevirisinde koyduğu "Lt. West:" etiketi.
            var speaker = pieces.Select(SubtitleSegmenter.LabelName).FirstOrDefault(name => name is { Length: > 0 })
                ?? SubtitleSegmenter.Split(turkish[i]).Select(SubtitleSegmenter.LabelName).FirstOrDefault(name => name is { Length: > 0 });
            var newTurn = pieces.Count > 0 && !pieces[0].Translate && "-–—".Contains(pieces[0].Text[0]);
            var spoken = turkish[i].Trim();
            if (speaker is not null && spoken.StartsWith(speaker + ":", StringComparison.OrdinalIgnoreCase))
                spoken = spoken[(speaker.Length + 1)..];
            spoken = spoken.TrimStart('-', '–', '—', ' ');
            var said = string.Join(' ', pieces.Where(piece => piece.Translate).Select(piece => piece.Text));
            if (spoken.Any(char.IsLetter)) lines.Add(new SpokenLine(said, spoken, speaker, newTurn));
        }
        return lines;
    }

    /// <summary>
    /// Dublaj yanıtı: altyazı metni ve her satırın seslendirilecek hali, tonu, konuşanın cinsiyeti. Satır sayısı
    /// tutmuyorsa ya da JSON bozuksa null (cihaz üstü çeviri kalır).
    /// </summary>
    internal static (string Translated, IReadOnlyList<SpokenLine> Speech)? ParseDubbing(string source, string json)
    {
        var english = source.Split('\n');
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var entries = document.RootElement.GetProperty("lines").EnumerateArray().ToList();
            if (entries.Count != english.Length) return null;
            var subtitles = new List<string>();
            var speech = new List<SpokenLine>();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var text = Clean(entry.GetProperty("text").GetString() ?? "").ReplaceLineEndings(" ");
                if (text.Length == 0) return null;
                var pieces = SubtitleSegmenter.Split(english[i]);
                // Konuşmacı: Claude'un bulduğu ad (iki noktasız adlar da: "Lt. West Okay…"), yoksa "AD:" etiketi.
                var named = entry.TryGetProperty("speaker", out var who) ? (who.GetString() ?? "").Trim().TrimEnd(':').Trim() : "";
                var speaker = named.Length > 0 ? named
                    : pieces.Select(SubtitleSegmenter.LabelName).FirstOrDefault(name => name is { Length: > 0 });
                if (speaker is not null && text.StartsWith(speaker + ":", StringComparison.OrdinalIgnoreCase))
                    text = text[(speaker.Length + 1)..].Trim();
                subtitles.Add(speaker is null ? text : $"{speaker}: {text}");
                // Oyun arayüzü yazıları (sayaç, silah adı, kayıt bildirimi) altyazıda kalır ama seslendirilmez.
                if (entry.TryGetProperty("dialogue", out var dialogue) && dialogue.ValueKind == System.Text.Json.JsonValueKind.False) continue;
                var newTurn = pieces.Count > 0 && !pieces[0].Translate && "-–—".Contains(pieces[0].Text[0]);
                var spoken = (entry.GetProperty("spoken").GetString() ?? "").Trim().TrimStart('-', '–', '—', ' ');
                if (!spoken.Any(char.IsLetter)) continue;
                var emotion = entry.GetProperty("emotion").GetString() switch
                {
                    "happy" => SpeechEmotion.Happy,
                    "sad" => SpeechEmotion.Sad,
                    "angry" => SpeechEmotion.Angry,
                    "surprised" => SpeechEmotion.Surprised,
                    "calm" => SpeechEmotion.Calm,
                    _ => (SpeechEmotion?)null,
                };
                var gender = entry.GetProperty("gender").GetString() switch
                {
                    "female" => VoiceGender.Female,
                    "male" => VoiceGender.Male,
                    _ => (VoiceGender?)null,
                };
                var said = string.Join(' ', pieces.Where(piece => piece.Translate).Select(piece => piece.Text));
                speech.Add(new SpokenLine(said, spoken, speaker, newTurn, emotion, gender));
            }
            return (string.Join('\n', subtitles), speech);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Önceki satırlar (bağlam), bilinen isimler ve çevrilecek metin.</summary>
    internal string UserMessage(string text)
    {
        var message = new StringBuilder();
        lock (_historyGate)
        {
            if (_history.Count > 0)
            {
                message.AppendLine("Earlier lines (context only):");
                foreach (var (source, translation) in _history)
                {
                    message.Append("EN: ").AppendLine(source.ReplaceLineEndings(" "));
                    message.Append("TR: ").AppendLine(translation.ReplaceLineEndings(" "));
                }
                message.AppendLine();
            }
        }
        if (!_names.IsEmpty) message.Append("Names: ").AppendLine(string.Join(", ", _names.Keys.Order(StringComparer.Ordinal))).AppendLine();
        message.AppendLine("Translate:");
        message.Append(text);
        return message.ToString();
    }

    /// <summary>Konuşmacı etiketlerindeki isimleri ("MOON: …") bir sonraki isteklerde tutarlı yazılsın diye saklar.</summary>
    private void RememberNames(string text)
    {
        foreach (var name in SubtitleSegmenter.Split(text).Select(SubtitleSegmenter.LabelName).OfType<string>())
        {
            var trimmed = name.Trim();
            if (trimmed.Length >= 2 && _names.Count < 64) _names[trimmed] = 0;
        }
    }

    /// <summary>Yanıttaki tırnakları ve boşlukları temizler.</summary>
    internal static string Clean(string reply)
    {
        var text = string.Join('\n', reply.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
        if (text.Length >= 2 && (text[0], text[^1]) is ('"', '"') or ('“', '”') or ('«', '»')) text = text[1..^1].Trim();
        return text;
    }
}
