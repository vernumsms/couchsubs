// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices.WindowsRuntime;
using CouchSubs.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace CouchSubs.App.Services;

/// <summary>
/// Windows'un cihaz üstü yazı tanıması. Oyun dilinin OCR paketi yoksa kullanıcının yüklü dillerinden
/// biri kullanılır (Latin alfabeli diller İngilizce altyazıyı da okuyabilir). Satırlar yukarıdan aşağı sıralanır.
/// </summary>
public sealed class WindowsOcrReader : ITextReader
{
    private readonly OcrEngine engine;

    private WindowsOcrReader(OcrEngine engine) => this.engine = engine;

    /// <summary>Hiçbir OCR dili yüklü değilse null.</summary>
    public static WindowsOcrReader? Create(string language)
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language(language))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages();
        return engine is null ? null : new WindowsOcrReader(engine);
    }

    /// <summary>Oyun dilinin kendi OCR paketi yüklü mü (değilse ayarlarda eklenmesi önerilir).</summary>
    public static bool HasLanguage(string language) => OcrEngine.IsLanguageSupported(new Language(language));

    public string Read(Frame image) => Read(image, deep: true);

    public string Read(Frame image, bool deep) => string.Join(' ', ReadLines(image, deep).Lines.Select(line => line.Text));

    public Reading ReadLines(Frame image, bool deep) => deep ? ReadingFallback.FirstLines(image, Recognize) : Recognize(image);

    /// <summary>
    /// Kare hem olduğu gibi hem de yalnız beyaz yazısı bırakılarak okunur; daha sözcüğe benzeyen okuma seçilir.
    /// Parlak sahnelerde ikincisi, renkli altyazılarda birincisi kazanır.
    /// </summary>
    private Reading Recognize(Frame image)
    {
        image = Enlarged(image);
        var raw = RecognizeOnce(image);
        if (image.Width < 40 || image.Height < 40) return raw;
        var whiteText = RecognizeOnce(TextImage.WhiteTextOnly(image));
        return OcrNoise.Score(whiteText.Text) > OcrNoise.Score(raw.Text) ? whiteText : raw;
    }

    /// <summary>
    /// Küçük görüntü (küçük Xbox penceresi, düşük çözünürlüklü akış) büyütülerek okunur: Windows yazı tanıması 20 pikselden
    /// alçak harflerde sözcükleri bozuyordu ("Ohr", "sne", "Donlt"). Hedef, 1080p'deki altyazı alanının yüksekliği.
    /// Kutular görüntüye oranla verildiği için büyütme yerleşimi değiştirmez.
    /// </summary>
    internal static Frame Enlarged(Frame image)
    {
        const int Target = 380;
        if (image.Height >= 300 || image.Height < 40) return image;
        var scale = Math.Min(2.0, (double)Target / image.Height);
        int width = (int)(image.Width * scale), height = (int)(image.Height * scale);
        if (width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension) return image;
        var source = image.Pixels;
        int sourceStride = image.Stride, stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(image.Height - 1.001, (y + 0.5) / scale - 0.5);
            if (sy < 0) sy = 0;
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, image.Height - 1);
            var fy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min(image.Width - 1.001, (x + 0.5) / scale - 0.5);
                if (sx < 0) sx = 0;
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, image.Width - 1);
                var fx = sx - x0;
                int a = y0 * sourceStride + x0 * 4, b = y0 * sourceStride + x1 * 4, c = y1 * sourceStride + x0 * 4, d = y1 * sourceStride + x1 * 4;
                var o = y * stride + x * 4;
                for (var k = 0; k < 4; k++)
                {
                    var top = source[a + k] + (source[b + k] - source[a + k]) * fx;
                    var bottom = source[c + k] + (source[d + k] - source[c + k]) * fx;
                    pixels[o + k] = (byte)(top + (bottom - top) * fy + 0.5);
                }
            }
        }
        return new Frame(pixels, width, height, stride);
    }

    private Reading RecognizeOnce(Frame image)
    {
        // OCR motoru çok küçük ya da çok büyük görüntüyü reddeder.
        if (image.Width < 40 || image.Height < 40 || image.Width > OcrEngine.MaxImageDimension || image.Height > OcrEngine.MaxImageDimension)
            return Reading.Empty;
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, image.Width, image.Height, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(image.Pixels.AsBuffer());
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        double width = image.Width, height = image.Height;
        var lines = result.Lines
            .SelectMany(line => Split(line, width, height))
            .OrderBy(line => line.Box?.Top ?? 0);
        return new Reading(lines);
    }

    /// <summary>
    /// Satırın kutusu sözcük kutularının birleşimidir; görüntünün kenarlarına oranla verilir. Windows aynı hizadaki
    /// uzak yazıları (soldaki menü, sağdaki gösterge) bazen tek satır sayar; aradaki boşluk bir sözcük boşluğu olamayacak
    /// kadar genişse satır orada ayrı satırlara bölünür (<see cref="Reading.IsSeparateText"/>).
    /// </summary>
    private static IEnumerable<TextLine> Split(OcrLine line, double width, double height)
    {
        var words = line.Words.ToList();
        if (words.Count == 0) yield break;
        var lineHeight = words.Max(word => word.BoundingRect.Height);
        var start = 0;
        for (var index = 1; index <= words.Count; index++)
        {
            var end = index == words.Count;
            if (!end && !Reading.IsSeparateText(words[index].BoundingRect.X - (words[index - 1].BoundingRect.X + words[index - 1].BoundingRect.Width), lineHeight, width))
                continue;
            var part = words.GetRange(start, index - start);
            TextBox? box = null;
            foreach (var word in part)
            {
                var r = word.BoundingRect;
                var wordBox = new TextBox(r.X / width, r.Y / height, (r.X + r.Width) / width, (r.Y + r.Height) / height);
                box = box?.Union(wordBox) ?? wordBox;
            }
            // Bölünmeyen satırın metni Windows'un verdiği gibi kalır (boşluksuz yazılan dillerde sözcükler birleşik).
            var text = start == 0 && end ? line.Text : string.Join(' ', part.Select(word => word.Text));
            yield return new TextLine(text.Trim(), box);
            start = index;
        }
    }
}
