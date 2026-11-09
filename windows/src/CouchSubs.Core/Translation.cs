// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core.Dubbing;

namespace CouchSubs.Core;

/// <summary>Bir satırı oyun dilinden hedef dile çevirir (cihaz üstünde).</summary>
public interface ITextTranslator
{
    Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Son çevrilen altyazının seslendirilecek cümleleri (konuşmacısıyla); dublaj için.</summary>
    IReadOnlyList<SpokenLine> SpokenLines(string text) => [];
}

/// <summary>
/// İki aşamalı çevirmen: önce hemen gösterilecek hızlı bir çeviri (cihaz üstü model), ardından daha iyi bir
/// çeviri (yapay zekâ). Pipeline hızlı çeviriyi gösterir; daha iyisi gelince satır hâlâ en son gösterilen satırsa
/// TV'deki yazıyı onunla değiştirir.
/// </summary>
public interface IProgressiveTranslator : ITextTranslator
{
    Task<ProgressiveTranslation> TranslateProgressivelyAsync(string text, CancellationToken cancellationToken = default);
}

/// <param name="Quick">Hemen gösterilecek çeviri.</param>
/// <param name="Refined">Daha iyi çeviri; iyileştirme yoksa ya da başarısız olduysa null döner. Hiçbir zaman hata fırlatmaz.</param>
public sealed record ProgressiveTranslation(string Quick, Task<string?> Refined)
{
    public static ProgressiveTranslation Final(string text) => new(text, Task.FromResult<string?>(null));
}

public enum TranslatorStatus
{
    Ready,
    /// <summary>Çeviri modeli henüz indirilmedi.</summary>
    NeedsDownload,
    /// <summary>Bu dil çifti desteklenmiyor.</summary>
    Unsupported,
}

/// <summary>Kullanıcının düzeltebileceği çeviri hatası (model eksik, dil desteklenmiyor).</summary>
public sealed class TranslatorException(TranslatorStatus status)
    : Exception($"Translator not available: {status}")
{
    public TranslatorStatus Status { get; } = status;
}

/// <summary>Çevrimiçi çeviri anahtarı (DeepL) denendiğinde ne çıktı.</summary>
public enum OnlineKeyState { Valid, Invalid, QuotaExceeded, Unreachable }

/// <param name="Used">Bu ay kullanılan karakter (geçerli anahtarda).</param>
/// <param name="Limit">Aylık sınır.</param>
public sealed record OnlineKeyCheck(OnlineKeyState State, long Used = 0, long Limit = 0);
