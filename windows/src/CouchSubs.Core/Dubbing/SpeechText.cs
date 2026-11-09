// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CouchSubs.Core.Dubbing;

/// <summary>Dublajda seslendirilecek bir cümle (Mac: CouchSubsCore/Dubbing/SpeechScript.swift).</summary>
/// <param name="Speaker">Altyazıdaki konuşmacı etiketi ("KRATOS: " → "KRATOS"); etiket yoksa null.</param>
/// <param name="NewTurn">Diyalog tiresiyle başlayan yeni konuşma: etiketsiz satırlarda konuşan değişti demektir.</param>
/// <param name="Emotion">Yapay zekânın sahneye bakarak seçtiği ton; null ise İngilizce metinden tahmin edilir.</param>
/// <param name="Gender">Yapay zekânın tahmin ettiği konuşmacı cinsiyeti; ses seçiminde kullanılır.</param>
/// <param name="Background">
/// Arka planda, başka bir konuşmayla aynı anda duyulan replik (telsiz, uzaktan bağırış, TV'deki spiker): sıra beklemez,
/// ana replikle birlikte daha kısık sesle çalınır.
/// </param>
public sealed record SpokenLine(string English, string Turkish, string? Speaker = null, bool NewTurn = false,
    SpeechEmotion? Emotion = null, VoiceGender? Gender = null, bool Background = false);

/// <summary>Pocket TTS Turkish'in duygu etiketleri; etiketsiz cümle düz okunur.</summary>
public enum SpeechEmotion { Happy, Sad, Angry, Surprised, Calm }

public static class SpeechEmotions
{
    public static string Tag(this SpeechEmotion emotion) => emotion switch
    {
        SpeechEmotion.Happy => "mutlu",
        SpeechEmotion.Sad => "üzgün",
        SpeechEmotion.Angry => "kızgın",
        SpeechEmotion.Surprised => "şaşkın",
        _ => "sakin",
    };

    private static readonly string[] Sad =
    [
        "i'm sorry", "i am sorry", "forgive me", "i miss", "she's gone", "he's gone", "they're gone", "is dead", "are dead",
        "goodbye", "farewell", "it's my fault", "i failed", "i couldn't save", "rest in peace",
    ];
    private static readonly string[] Surprised = ["what", "whoa", "wow", "huh", "impossible", "no way", "how", "wait", "look out", "what's that"];
    private static readonly string[] Happy =
    [
        "we did it", "yes", "finally", "haha", "ha", "hooray", "great", "amazing", "perfect", "thank you", "thanks",
        "welcome back", "well done", "nice", "awesome",
    ];
    private static readonly string[] Angry =
    [
        "stop", "enough", "get back", "get out", "back off", "shut up", "fool", "idiot", "damn", "die", "kill", "never",
        "how dare", "coward", "leave", "move", "now", "fight", "run",
    ];
    private static readonly string[] Calm =
    [
        "easy", "calm down", "breathe", "take your time", "it's okay", "it's alright", "don't worry", "rest", "slowly",
        "quiet", "shh", "listen to me",
    ];

    /// <summary>Cümlenin İngilizcesine bakarak duygu tahmini; emin olunmayan cümle düz okunur (Mac ile aynı kurallar).</summary>
    public static SpeechEmotion? Guess(string english)
    {
        var text = english.ToLowerInvariant();
        var words = Regex.Split(text, @"[^\p{L}'’]+").Where(word => word.Length > 0).ToHashSet(StringComparer.Ordinal);
        bool Has(string[] phrases) => phrases.Any(phrase => phrase.Contains(' ') ? text.Contains(phrase, StringComparison.Ordinal) : words.Contains(phrase));

        if (text.Contains("?!", StringComparison.Ordinal) || text.Contains("!?", StringComparison.Ordinal)) return SpeechEmotion.Surprised;
        if (Has(Sad)) return SpeechEmotion.Sad;
        if (text.Contains('!'))
        {
            if (Has(Surprised)) return SpeechEmotion.Surprised;
            if (Has(Happy)) return SpeechEmotion.Happy;
            if (Has(Angry)) return SpeechEmotion.Angry;
            return null;
        }
        return Has(Calm) ? SpeechEmotion.Calm : null;
    }
}

/// <summary>Türkçe cümleyi Pocket TTS Turkish'e hazırlar: sayılar yazıyla, her cümle küçük harfle, duygu etiketi başta.</summary>
public static partial class SpeechText
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static string Prepare(string turkish, SpeechEmotion? emotion = null)
    {
        var text = turkish.Replace("…", "...").Replace('“', '"').Replace('”', '"').Replace('’', '\'');
        text = TurkishNumbers.SpellOut(text);
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // "?!" ve "!!!" gibi yığılmış işaretler modeli erken susturabiliyor; tek işarete iner.
        text = StackedQuestion().Replace(text, "?");
        text = StackedExclamation().Replace(text, "!");
        text = LowercaseSentenceStarts(text);
        return emotion is { } value ? $"[{value.Tag()}] {text}" : text;
    }

    /// <summary>Her cümlenin ilk harfini Türkçe kurala göre küçültür (I → ı, İ → i); model böyle eğitilmiş.</summary>
    public static string LowercaseSentenceStarts(string text)
    {
        var output = new StringBuilder(text.Length);
        var atStart = true;
        foreach (var character in text)
        {
            if (atStart && char.IsLetter(character))
            {
                output.Append(char.ToLower(character, Turkish));
                atStart = false;
            }
            else
            {
                output.Append(character);
                if (character is '.' or '!' or '?') atStart = true;
            }
        }
        return output.ToString();
    }

    [GeneratedRegex(@"[?!]*\?[?!]*")]
    private static partial Regex StackedQuestion();

    [GeneratedRegex(@"!{2,}")]
    private static partial Regex StackedExclamation();
}

/// <summary>Sayıları Türkçe yazıya çevirir: "1250" → "bin iki yüz elli", "3,5" → "üç virgül beş", "%20" → "yüzde yirmi".</summary>
public static partial class TurkishNumbers
{
    private static readonly string[] Ones = ["", "bir", "iki", "üç", "dört", "beş", "altı", "yedi", "sekiz", "dokuz"];
    private static readonly string[] Tens = ["", "on", "yirmi", "otuz", "kırk", "elli", "altmış", "yetmiş", "seksen", "doksan"];
    private static readonly string[] Scales = ["", "bin", "milyon", "milyar", "trilyon"];

    public static string Words(long number)
    {
        if (number == 0) return "sıfır";
        if (number < 0) return "eksi " + Words(-number);
        var groups = new List<int>();
        for (var rest = number; rest > 0; rest /= 1000) groups.Add((int)(rest % 1000));
        if (groups.Count > Scales.Length) return number.ToString(CultureInfo.InvariantCulture);
        var parts = new List<string>();
        for (var scale = groups.Count - 1; scale >= 0; scale--)
        {
            var group = groups[scale];
            if (group == 0) continue;
            // "bir bin" denmez: 1000 → "bin".
            if (scale == 1 && group == 1) { parts.Add("bin"); continue; }
            parts.Add(Hundreds(group));
            if (scale > 0) parts.Add(Scales[scale]);
        }
        return string.Join(' ', parts);
    }

    private static string Hundreds(int number)
    {
        var parts = new List<string>();
        int hundred = number / 100, ten = number % 100 / 10, one = number % 10;
        if (hundred > 0) parts.Add(hundred == 1 ? "yüz" : Ones[hundred] + " yüz");
        if (ten > 0) parts.Add(Tens[ten]);
        if (one > 0) parts.Add(Ones[one]);
        return string.Join(' ', parts);
    }

    public static string SpellOut(string text) => NumberPattern().Replace(text, match =>
    {
        if (!long.TryParse(match.Groups[2].Value.Replace(".", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return match.Value;
        var spoken = Words(value);
        if (match.Groups[4].Success)
        {
            var fraction = match.Groups[4].Value;
            var rest = long.Parse(fraction, CultureInfo.InvariantCulture);
            if (match.Groups[3].Value == ":")
            {
                // Saat: "14:30" → "on dört otuz", "09:05" → "dokuz sıfır beş".
                if (rest > 0) spoken += " " + (fraction.StartsWith('0') ? "sıfır " : "") + Words(rest);
            }
            else
            {
                var zeros = fraction.TakeWhile(c => c == '0').Count();
                spoken += " virgül " + string.Join(' ', Enumerable.Repeat("sıfır", zeros));
                if (rest > 0) spoken += (zeros > 0 ? " " : "") + Words(rest);
                spoken = spoken.TrimEnd();
            }
        }
        if (match.Groups[1].Success || match.Groups[5].Success) spoken = "yüzde " + spoken;
        return spoken;
    });

    [GeneratedRegex(@"(%)?(\d{1,3}(?:\.\d{3})+|\d+)(?:([,:])(\d+))?(%)?")]
    private static partial Regex NumberPattern();
}
