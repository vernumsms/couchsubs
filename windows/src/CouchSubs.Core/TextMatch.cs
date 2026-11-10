// SPDX-License-Identifier: AGPL-3.0-only

using System.Globalization;
using System.Text;

namespace CouchSubs.Core;

/// <summary>OCR çıktılarını karşılaştırmak için sadeleştirme ve gürültüye dayanıklı benzerlik.</summary>
public static class TextMatch
{
    /// <summary>Küçük harf, yalnız harf/rakam, tek boşluk.</summary>
    public static string Normalize(string s)
    {
        var builder = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            builder.Append(IsAlphanumeric(c) ? c : ' ');
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Levenshtein oranı <paramref name="threshold"/> ve üstündeyse benzer sayılır. Boş metin hiçbir metne benzemez.</summary>
    public static bool IsSimilar(string a, string b, double threshold = 0.85)
    {
        if (a == b) return a.Length > 0;
        if (a.Length == 0 || b.Length == 0) return false;
        return 1 - (double)Distance(a, b) / Math.Max(a.Length, b.Length) >= threshold;
    }

    /// <summary>Levenshtein uzaklığı: birini ötekine çeviren en az harf ekleme/silme/değiştirme sayısı.</summary>
    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var row = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
                row[j] = Math.Min(Math.Min(previous[j] + 1, row[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, row) = (row, previous);
        }
        return previous[b.Length];
    }

    /// <summary>
    /// Yeni okuma, gönderilmiş altyazının yeniden okuması mı (sadeleştirilmiş metinler). Süslü yazı tipinde ya da
    /// solarken OCR bir iki sözcüğü her karede başka çöpe çevirir ("ou$3e", "waters2aljd") ya da iki satırlık
    /// altyazının bir satırını kaçırır; karakter benzerliği düşse de sözcükler aynı kalır. Rakam karışmış sözcükler
    /// ve tek harfler çöp sayılıp karşılaştırmaya girmez. Metin belirgin uzadıysa (daktilo efekti) yeni satırdır.
    /// </summary>
    public static bool IsSameSubtitle(string reread, string sent)
    {
        if (IsSimilar(reread, sent)) return true;
        var x = CleanWords(reread);
        var remaining = CleanWords(sent);
        var sentCount = remaining.Count;
        var shared = 0;
        foreach (var word in x)
            if (remaining.Remove(word)) shared++;
        // Kaçırılmış satır: yeni okumanın sözcüklerinin hemen hepsi gönderilende var.
        if (x.Count >= 4 && x.Count <= sentCount && shared >= x.Count * 0.85) return true;
        if (IsFadingReread(x, CleanWords(sent))) return true;
        // Kısa satırda tek sözcük anlamı değiştirir ("where is the boy" / "ship"); orada yalnız karakter benzerliği geçerli.
        int longer = Math.Max(x.Count, sentCount), shorter = Math.Min(x.Count, sentCount);
        if (shorter < 6 || (double)shorter / longer < 0.75) return false;
        return (double)shared / longer >= 0.75;
    }

    /// <summary>
    /// Altyazı solarken ya da belirirken yazı tanıma sözcükleri harf harf bozar: "Tell me how he died" →
    /// "lell now he died". Sözcükler gönderilen satırdakilerle aynı sırada, ya birebir ya da birkaç harfi bozulmuş
    /// hâlde duruyorsa yeni satır değildir. Kural bilerek sıkı: gevşek hâli (claude.13) farklı cümleleri de aynı
    /// sanıyordu ("is that all of them" / "what do we call you then") ve hafızadaki yanlış çeviri gösteriliyordu.
    /// Yanlış çeviri göstermektense bozuk satırı bir kez daha çevirtmek iyidir.
    /// </summary>
    private static bool IsFadingReread(List<string> reread, List<string> sent)
    {
        if (reread.Count < 4 || reread.Count > sent.Count + 1) return false;
        int shared = 0, misread = 0, next = 0;
        var strong = false;
        foreach (var word in reread)
        {
            // Sözcük, gönderilen satırda bir önceki eşleşmenin ilerisinde aranır (sıra korunmalı).
            for (var k = next; k < sent.Count; k++)
            {
                var other = sent[k];
                if (word == other) { shared++; next = k + 1; break; }
                if (word.Length < 3 || other.Length < 3) continue;
                var distance = Distance(word, other);
                // En çok %40'ı bozulmuş: "hanqinc" / "hanging", "musl" / "must"; "that" / "then" (%50) sayılmaz.
                if (distance / (double)Math.Max(word.Length, other.Length) > 0.4) continue;
                misread++;
                next = k + 1;
                if (word.Length >= 5 && distance >= 2) strong = true;
                break;
            }
        }
        // Bir iki harf farkı anlam değiştirebilir ("east" / "west"): en az iki bozuk sözcük ya da çok bozulmuş uzun bir sözcük gerekir.
        if (shared == 0 || misread == 0 || (misread < 2 && !strong)) return false;
        return shared + misread >= reread.Count * 0.75;
    }

    // Tek harfler ("a", "I" ya da OCR'ın uydurduğu "O") karşılaştırmaya bir şey katmaz.
    private static List<string> CleanWords(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1 && !w.Any(char.IsNumber)).ToList();

    private static bool IsAlphanumeric(char c) => char.GetUnicodeCategory(c) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };
}
