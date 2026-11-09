// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;

namespace CouchSubs.Core.Dubbing;

/// <summary>Dublaj sesleri: Pocket TTS Turkish'in hazır sesleri (yapay, gerçek kişi değil).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DubbingVoice>))]
public enum DubbingVoice { Female1, Female2, Female3, Male1, Male2, Male3 }

public enum VoiceGender { Female, Male }

public static class DubbingVoices
{
    public static readonly DubbingVoice[] All = Enum.GetValues<DubbingVoice>();

    /// <summary>Modeldeki dosya adı ("female_1").</summary>
    public static string FileName(this DubbingVoice voice) => voice switch
    {
        DubbingVoice.Female1 => "female_1",
        DubbingVoice.Female2 => "female_2",
        DubbingVoice.Female3 => "female_3",
        DubbingVoice.Male1 => "male_1",
        DubbingVoice.Male2 => "male_2",
        _ => "male_3",
    };

    public static VoiceGender Gender(this DubbingVoice voice) =>
        voice is DubbingVoice.Female1 or DubbingVoice.Female2 or DubbingVoice.Female3 ? VoiceGender.Female : VoiceGender.Male;
}

/// <summary>
/// Karakterlere ses dağıtır (Mac: VoiceCast.swift). Her konuşmacı adı kalıcı olarak bir sese bağlanır; etiketsiz
/// satırlar anlatıcı sesiyle, tireyle değişen konuşmalar iki ses arasında sırayla okunur.
/// </summary>
public sealed class VoiceCast
{
    /// <summary>Adın büyük harfli biçimi → ses. Ayarlardan değiştirilebilir.</summary>
    public Dictionary<string, DubbingVoice> Assignments { get; init; } = new(StringComparer.Ordinal);
    public DubbingVoice Narrator { get; set; } = DubbingVoice.Male1;
    /// <summary>Tireli konuşmada sıradaki kişinin sesi.</summary>
    public DubbingVoice Partner { get; set; } = DubbingVoice.Female1;

    private bool _lastWasPartner;

    public static string Key(string name) => name.ToUpperInvariant();

    /// <summary>Satırın sesi; yeni bir ad ilk kez görülünce ona bir ses ayrılır (değişti: true).</summary>
    public DubbingVoice VoiceFor(SpokenLine line, out bool assigned)
    {
        assigned = false;
        if (line.Speaker is { } speaker)
        {
            _lastWasPartner = false;
            var key = Key(speaker);
            if (Assignments.TryGetValue(key, out var known)) return known;
            // Ad listesinde yoksa yapay zekânın bağlamdan tahmin ettiği cinsiyet; o da bilinmiyorsa anlatıcının sesiyle
            // aynı cinsiyet (Call of Duty'de "Moon", "Jae" gibi belirsiz adlı askerlere kadın sesi gidiyordu).
            var voice = Pick(NameGender.Guess(speaker) ?? line.Gender ?? Narrator.Gender());
            Assignments[key] = voice;
            assigned = true;
            return voice;
        }
        if (line.NewTurn) _lastWasPartner = !_lastWasPartner;
        var turn = _lastWasPartner ? Partner : Narrator;
        // Etiketsiz satırda konuşanın cinsiyeti biliniyorsa ona uyan ses: önce sıradaki, sonra diğeri, sonra herhangi biri.
        if (line.Gender is { } gender && turn.Gender() != gender)
        {
            var other = _lastWasPartner ? Narrator : Partner;
            return other.Gender() == gender ? other : DubbingVoices.All.First(voice => voice.Gender() == gender);
        }
        return turn;
    }

    public void Assign(DubbingVoice voice, string name) => Assignments[Key(name)] = voice;

    public VoiceCast Copy() => new()
    {
        Assignments = new Dictionary<string, DubbingVoice>(Assignments, StringComparer.Ordinal),
        Narrator = Narrator,
        Partner = Partner,
    };

    /// <summary>Yeni karakter için en az kullanılan ses; anlatıcı sesi en sona kalır.</summary>
    private DubbingVoice Pick(VoiceGender? gender)
    {
        var uses = DubbingVoices.All.ToDictionary(voice => voice, _ => 0);
        foreach (var voice in Assignments.Values) uses[voice]++;
        uses[Narrator]++;
        return DubbingVoices.All
            .Where(voice => gender is null || voice.Gender() == gender)
            .OrderBy(voice => uses[voice]).ThenBy(voice => (int)voice)
            .First();
    }
}

/// <summary>Addan cinsiyet tahmini: oyun karakterleri ve yaygın İngilizce adlar (Mac ile aynı listeler).</summary>
public static class NameGender
{
    private static readonly HashSet<string> Female = new(StringComparer.Ordinal)
    {
        "freya", "faye", "angrboda", "sif", "thrud", "ellie", "abby", "dina", "tess", "marlene", "riley", "aloy", "elisabet",
        "beta", "mary", "jane", "mj", "may", "rio", "hailey", "lara", "jill", "claire", "ada", "ashley", "tifa", "aerith",
        "yuffie", "jessie", "yuna", "lulu", "rikku", "ciri", "yennefer", "triss", "kassandra", "aya", "evie", "lydia",
        "anna", "emma", "olivia", "sophia", "isabella", "mia", "amelia", "harper", "evelyn", "abigail", "emily", "ella",
        "elizabeth", "sofia", "avery", "scarlett", "grace", "chloe", "victoria", "lily", "hannah", "zoe", "nora", "sarah",
        "rachel", "rebecca", "laura", "julia", "maria", "kate", "katie", "lucy", "alice", "eve", "helen", "linda", "susan",
        "karen", "nancy", "lisa", "betty", "margaret", "sandra", "kim", "mother", "mom", "mum", "queen", "princess", "lady",
        "girl", "woman", "sister", "aunt", "grandma", "witch", "goddess", "daughter", "wife",
    };

    private static readonly HashSet<string> Male = new(StringComparer.Ordinal)
    {
        "kratos", "atreus", "mimir", "baldur", "thor", "odin", "brok", "sindri", "tyr", "heimdall", "freyr", "durlin", "joel",
        "tommy", "bill", "henry", "jesse", "lev", "isaac", "owen", "manny", "jerry", "rost", "erend", "varl", "sylens",
        "kotallo", "peter", "miles", "harry", "otto", "kraven", "venom", "norman", "aaron", "ganke", "leon", "chris",
        "albert", "wesker", "ethan", "cloud", "barret", "zack", "sephiroth", "noctis", "tidus", "auron", "geralt",
        "jaskier", "alexios", "bayek", "ezio", "altair", "connor", "edward", "arthur", "dutch", "john", "micah", "hosea",
        "nathan", "nate", "drake", "sully", "jin", "khotun", "deacon", "boozer", "james", "robert", "michael", "william",
        "david", "richard", "joseph", "thomas", "charles", "daniel", "matthew", "anthony", "mark", "paul", "steven",
        "andrew", "kenneth", "kevin", "brian", "george", "jack", "jacob", "luke", "max", "father", "dad", "boy", "king",
        "prince", "lord", "sir", "man", "brother", "uncle", "grandpa", "son", "husband", "soldier", "guard", "captain",
        "commander", "general",
    };

    public static VoiceGender? Guess(string name)
    {
        var lower = name.ToLowerInvariant();
        if (Female.Contains(lower)) return VoiceGender.Female;
        if (Male.Contains(lower)) return VoiceGender.Male;
        // Çok sözcüklü adda sözcüklere bakılır ("OLD MAN", "Mary Jane").
        foreach (var word in lower.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Female.Contains(word)) return VoiceGender.Female;
            if (Male.Contains(word)) return VoiceGender.Male;
        }
        return null;
    }
}
