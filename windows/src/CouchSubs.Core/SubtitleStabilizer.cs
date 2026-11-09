// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core;

public enum StabilizerKind { None, Translate, Clear }

/// <summary>Okuma hattının bir kareye vereceği tepki.</summary>
public readonly record struct StabilizerAction(StabilizerKind Kind, string? Text = null)
{
    public static readonly StabilizerAction None = new(StabilizerKind.None);
    public static readonly StabilizerAction Clear = new(StabilizerKind.Clear);
    public static StabilizerAction Translate(string text) => new(StabilizerKind.Translate, text);
}

/// <summary>
/// Kare kare gelen OCR çıktısını izler; altyazı kararlı hale gelince çevrilmesini,
/// ekrandan kalkınca TV'den temizlenmesini ister. Aynı altyazıyı tekrar göndermez.
/// </summary>
/// <param name="fastSentences">
/// true ise noktalamayla biten satır ilk karede gönderilir. false ise her satır <paramref name="stableFrames"/> kare
/// aynı okunmalıdır: yazı belirirken ya da görüntü bozulurken okunan çöp satırlar ("V'.lfiere, Ibe, flick") iki karede
/// aynı çıkmadığı için elenir.
/// </param>
public sealed class SubtitleStabilizer(double similarity = 0.85, int stableFrames = 2, int emptyFramesToClear = 2, bool fastSentences = true)
{
    private string candidate = "";
    private int candidateCount;
    private string sent = "";
    private int emptyCount;

    public StabilizerAction Feed(string rawText)
    {
        var text = TextMatch.Normalize(rawText);
        // 3 karakterden kısa okumalar (gürültü, tek simge) boş kare sayılır.
        if (text.Length < 3)
        {
            candidate = "";
            candidateCount = 0;
            emptyCount++;
            if (emptyCount == emptyFramesToClear && sent.Length > 0)
            {
                sent = "";
                return StabilizerAction.Clear;
            }
            return StabilizerAction.None;
        }

        emptyCount = 0;
        if (TextMatch.IsSimilar(text, candidate, similarity))
        {
            candidateCount++;
        }
        else
        {
            candidate = text;
            candidateCount = 1;
        }
        // Daktilo efektinde metin büyürken aday değişir; tamamlanıp kararlı olunca gider.
        // Noktalamayla biten satır zaten tamamdır; hızlı konuşmada beklemeden ilk karede gider.
        var ready = candidateCount == stableFrames || (fastSentences && candidateCount == 1 && EndsSentence(rawText));
        // Gönderilenin gürültülü yeniden okuması yeni satır değildir; TV'de aynı cümle iki kez görünmesin.
        if (!ready || TextMatch.IsSimilar(text, sent, similarity) || TextMatch.IsSameSubtitle(text, sent)) return StabilizerAction.None;
        sent = text;
        return StabilizerAction.Translate(rawText.Trim());
    }

    /// <summary>Satır nokta, ünlem, soru ya da üç noktayla bitiyor mu (kapanan tırnak ve parantezler yok sayılır).</summary>
    public static bool EndsSentence(string raw)
    {
        var text = raw.Trim().TrimEnd('"', '\'', '”', '’', '»', ')', ']');
        return text.Length > 0 && ".!?…".Contains(text[^1]);
    }

    /// <summary>Çeviri kaynağıyla neredeyse aynıysa metin zaten hedef dildedir (ör. konsolun Türkçe menüleri).</summary>
    public bool ShouldSend(string source, string translation) =>
        !TextMatch.IsSimilar(TextMatch.Normalize(source), TextMatch.Normalize(translation), 0.90);

    /// <summary>Son gönderilen satırı unutur (ör. çevirisi başarısız oldu); satır ekranda kaldıkça yeniden kararlı sayılıp çevrilir.</summary>
    public void ForgetSent()
    {
        sent = "";
        candidate = "";
        candidateCount = 0;
    }

    public StabilizerAction Reset()
    {
        candidate = "";
        candidateCount = 0;
        emptyCount = 0;
        if (sent.Length == 0) return StabilizerAction.None;
        sent = "";
        return StabilizerAction.Clear;
    }
}
