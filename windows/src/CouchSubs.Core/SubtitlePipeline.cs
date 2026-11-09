// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core.Dubbing;

namespace CouchSubs.Core;

/// <summary>Kare → bölge → yazı tanıma → kararlılık → çeviri → TV.</summary>
/// <param name="instant">
/// Anlık altyazı: TV'de yalnız oyunda o an duran satırın çevirisi görünür. Yeni satır gelince eskisi okunmamış olsa
/// da hemen kalkar; ekrandan kalkmış ya da yerini yenisine bırakmış satırın geç gelen çevirisi hiç gösterilmez.
/// Hızlı sahnelerde altyazılar birbirine karışmaz.
/// </param>
public sealed class SubtitlePipeline(ITextReader reader, ITextTranslator translator, ISubtitleSender sender, ReadScheduler? scheduler = null,
    bool instant = false)
{
    private readonly object gate = new();
    /// <summary>Varsayılanı her kareyi okur; uygulama gereksiz okumaları atlayan planı verir.</summary>
    private readonly ReadScheduler schedule = scheduler ?? ReadScheduler.Always();
    /// <summary>Kareler saniyede ~20 kez okunur; yaklaşık 0,3 sn boş kalan bölge altyazının kalktığı anlamına gelir.</summary>
    private readonly SubtitleStabilizer stabilizer = new(emptyFramesToClear: 6);
    private readonly Dictionary<int, Task> translations = [];
    /// <summary>
    /// Her çeviri isteği sıra numarası alır. Hızlı konuşmada çeviriler sırasız dönebilir:
    /// daha yeni bir satırın çevirisi gösterilmediyse eski satırınki yine gösterilir.
    /// </summary>
    private int lastRequested;
    private int lastShown;
    /// <summary>Son satırın TV'ye gönderildiği an (ms); iyileştirilmiş çevirinin hâlâ gösterilip gösterilmeyeceği buna bakar.</summary>
    private long lastShownAt;
    /// <summary>Şu an oyun ekranında duran satırın numarası (bölge boşalınca null).</summary>
    private int? lineOnScreen;
    /// <summary>Okuma durdurulunca artar; önceki oturumdan dönen çeviriler gösterilmez.</summary>
    private int session;
    /// <summary>Son okunan satırlar; kare değişmediyse yeniden okunmaz, bunlar kullanılır.</summary>
    private Reading lastReading = Reading.Empty;

    public (string Source, string Translation)? LastPair { get; private set; }
    /// <summary>Son gösterilen çevirinin oyundaki altyazısının karedeki yeri.</summary>
    public TextBox? LastBox { get; private set; }
    public TranslatorStatus? LastError { get; private set; }

    /// <summary>Yeni çeviri gösterilince çağrılır (kaynak, çeviri).</summary>
    public event Action<string, string>? PairShown;
    /// <summary>TV'ye giden her yeni altyazının seslendirilecek cümleleri (dublaj).</summary>
    public event Action<IReadOnlyList<SpokenLine>>? SpeechReady;
    /// <summary>Her çeviri denemesinden sonra çağrılır (başarılıysa null).</summary>
    public event Action<TranslatorStatus?>? TranslationResult;

    /// <summary>Okuma çeviriyi beklemez; böylece çeviri sürerken gelen satırlar da yakalanır.</summary>
    public async Task ProcessAsync(Frame frame, CropRegion region)
    {
        // Yazı tanıma uzun sürebilir; çağıranı (arayüzü) bekletmemek için iş parçacığı havuzunda yapılır.
        var raw = await Task.Run(() =>
        {
            try
            {
                var area = RegionCropper.Crop(frame, region);
                ReadPlan plan;
                lock (gate)
                {
                    plan = schedule.Plan(RegionCropper.Signature(area));
                    if (plan.Reuse) return lastReading;
                }
                var read = reader.ReadLines(area, plan.Deep);
                lock (gate)
                {
                    schedule.Record(read.Text);
                    lastReading = read;
                }
                return read;
            }
            catch { return Reading.Empty; }
        });
        // Kumanda ipuçları ("R2 Swing") altyazı değildir; çevrilmez, yeri de altyazının yerine katılmaz.
        // Yazı tanımanın uydurduğu sözcük çöpleri ("ou$3e") de çevirmene gitmez.
        // Alandaki menü ve köşe göstergeleri de altyazı değildir; yalnız altyazı bloğu çevrilir ve kutusu ona göre olur.
        var area = new TextBox(region.Left, region.Top, region.Right, region.Bottom);
        var reading = raw.WithoutButtonPrompts().WithoutOcrNoise().SubtitleBlock(area);

        lock (gate)
        {
            var action = stabilizer.Feed(reading.Text);
            switch (action.Kind)
            {
                case StabilizerKind.Clear:
                    lineOnScreen = null;
                    sender.Clear();
                    break;
                case StabilizerKind.Translate:
                    // Anlık modda yeni satır gelir gelmez eskisinin çevirisi kalkar; yenisinin çevirisi ardından gelir.
                    if (instant && lastShown > 0) sender.Clear();
                    var number = ++lastRequested;
                    var current = session;
                    lineOnScreen = number;
                    // Çevirmen burada, istek sırasıyla çağrılır (ayrı görevle başlatılsa sıra karışabilirdi).
                    // Çeviri hemen bittiyse kendini zaten listeden çıkarmıştır; yeniden eklenmez.
                    var box = reading.Box?.PlacedIn(area).CenteredOnScreen();
                    var task = TranslateAndForget(action.Text!, box, number, current);
                    if (!task.IsCompleted) translations[number] = task;
                    break;
            }
        }
    }

    /// <summary>Süren bütün çevirilerin bitmesini bekler.</summary>
    public async Task WaitForTranslationsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (gate) pending = [.. translations.Values];
            if (pending.Length == 0) return;
            await Task.WhenAll(pending);
        }
    }

    /// <summary>Satır ekrandan kalktıktan sonra gelen çevirinin TV'de kalacağı süre (TV uygulamasıyla aynı kural).</summary>
    public static int ReadingTime(string text) => Math.Min(8000, 2000 + (text.Length * 60));

    private async Task TranslateAndForget(string source, TextBox? box, int number, int current)
    {
        await Translate(source, box, number, current);
        lock (gate) translations.Remove(number);
    }

    private async Task Translate(string source, TextBox? box, int number, int current)
    {
        string translated;
        Task<string?>? refined = null;
        try
        {
            if (translator is IProgressiveTranslator progressive)
            {
                var result = await progressive.TranslateProgressivelyAsync(source);
                translated = result.Quick;
                refined = result.Refined;
            }
            else translated = await translator.TranslateAsync(source);
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (current != session) return;
                // Model eksikse ya da geçici bir hata olduysa satır ekranda kaldıkça yeniden denenir.
                LastError = (error as TranslatorException)?.Status;
                if (number == lineOnScreen) stabilizer.ForgetSent();
            }
            TranslationResult?.Invoke(LastError);
            return;
        }

        lock (gate)
        {
            if (current != session) return;
            LastError = null;
        }
        TranslationResult?.Invoke(null);

        lock (gate)
        {
            if (number <= lastShown || !stabilizer.ShouldSend(source, translated)) return;
            // Anlık modda yalnız oyunda hâlâ duran satır gösterilir.
            if (instant && number != lineOnScreen) return;
            lastShown = number;
            lastShownAt = Environment.TickCount64;
            // Satır hâlâ ekrandaysa kalkınca silinir; kalktıysa okunacak kadar gösterilir.
            sender.SendSubtitle(translated, number == lineOnScreen ? 15000 : ReadingTime(translated), box);
            LastPair = (source, translated);
            LastBox = box;
        }
        PairShown?.Invoke(source, translated);
        // Dublaj iyileştirilmiş çeviriyi bekler (varsa); yoksa hızlı çeviri hemen seslendirilir.
        if (refined is null)
        {
            if (SpeechReady is { } speech) speech(translator.SpokenLines(source));
            return;
        }
        await ShowRefined(source, translated, refined, box, number, current);
    }

    /// <summary>
    /// Daha iyi çeviri gelince, satır hâlâ TV'deki en son satırsa (arada yenisi gösterilmediyse) yazıyı değiştirir.
    /// Satır ekrandan çoktan kalktıysa ve okuma süresi geçtiyse yeniden gösterilmez.
    /// </summary>
    private async Task ShowRefined(string source, string quick, Task<string?> refined, TextBox? box, int number, int current)
    {
        var better = await refined;
        var changed = false;
        lock (gate)
        {
            if (current == session && number == lastShown && better is { Length: > 0 } && better != quick
                && (number == lineOnScreen || (!instant && Environment.TickCount64 - lastShownAt < ReadingTime(quick))))
            {
                sender.SendSubtitle(better, number == lineOnScreen ? 15000 : ReadingTime(better), box);
                LastPair = (source, better);
                changed = true;
            }
        }
        if (changed) PairShown?.Invoke(source, better!);
        if (SpeechReady is { } speech) speech(translator.SpokenLines(source));
    }

    /// <summary>Okuma durunca TV'deki son altyazıyı kaldırır.</summary>
    public void Reset()
    {
        lock (gate)
        {
            session++;
            lineOnScreen = null;
            lastReading = Reading.Empty;
            schedule.Reset();
            if (stabilizer.Reset() == StabilizerAction.Clear) sender.Clear();
        }
    }
}
