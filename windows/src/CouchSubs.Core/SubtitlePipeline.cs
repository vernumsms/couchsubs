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
    /// <summary>Anlık modda her satır iki kare aynı okunmalıdır (bozuk okumalar elenir; bir kare ≈ 50 ms).</summary>
    private readonly SubtitleStabilizer stabilizer = new(emptyFramesToClear: 6, fastSentences: !instant);
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
        // Mermi/puan sayaçları da altyazı değildir (her atışta değişip yeniden çevriliyordu).
        var reading = raw.WithoutButtonPrompts().WithoutPromptWords().WithoutOcrNoise().WithoutHudNumbers().SubtitleBlock(area);

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
                    // Çevirisi henüz sürmekte olan replik yeniden okunduysa (konuşmacı adı bir görünüp bir kayboldu,
                    // yazı tanıma bir harfi farklı okudu) yeni istek yapılmaz; ilk çevirisi beklenir. Denemede aynı
                    // replik bir saniyede yedi kez çevriliyor, altyazı da her seferinde değişiyordu.
                    var key = TextMatch.Normalize(action.Text!);
                    var now = Environment.TickCount64;
                    var seen = FindRecent(key, now);
                    var fresh = seen is not null && now - seen.SeenAt <= RecentMs;
                    if (fresh && seen!.Translation.Length == 0) break;
                    if (seen is null)
                    {
                        recent.Add(new RecentLine { Key = key, SeenAt = now });
                        if (recent.Count > 32) recent.RemoveAt(0);
                    }
                    // Anlık modda yeni satır gelir gelmez eskisinin çevirisi kalkar; yenisinin çevirisi ardından gelir.
                    if (instant && lastShown > 0) sender.Clear();
                    var number = ++lastRequested;
                    var current = session;
                    lineOnScreen = number;
                    // Çevirmen burada, istek sırasıyla çağrılır (ayrı görevle başlatılsa sıra karışabilirdi).
                    // Çeviri hemen bittiyse kendini zaten listeden çıkarmıştır; yeniden eklenmez.
                    var box = reading.Box?.PlacedIn(area).CenteredOnScreen();
                    // Az önce çevrilen replik yeniden okunduysa (altyazı titredi, yazı tanıma bir harfi farklı okudu,
                    // konuşmacı adı sonradan belirdi) çevirisi yeniden istenmez; kayıtlı çeviri gösterilir.
                    var known = fresh ? seen!.Translation : null;
                    var task = TranslateAndForget(action.Text!, box, number, current, known);
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

    private async Task TranslateAndForget(string source, TextBox? box, int number, int current, string? known)
    {
        await Translate(source, box, number, current, known);
        lock (gate) translations.Remove(number);
    }

    // --- Yakın zamanda çevrilen ve seslendirilen replikler ---

    /// <summary>Bu süre içinde yeniden okunan aynı replik yeniden çevrilmez ve seslendirilmez.</summary>
    private const int RecentMs = 8000;

    private sealed class RecentLine
    {
        public required string Key { get; init; }
        public string Translation { get; set; } = "";
        public long SeenAt { get; set; }
        public long SpokenAt { get; set; } = long.MinValue / 2;
    }

    private readonly List<RecentLine> recent = [];

    /// <summary>
    /// Aynı replik mi: karakter/sözcük benzerliği ya da biri ötekinin sonu ("here take my radio" ile konuşmacı adı
    /// sonradan beliren "moon here take my radio").
    /// </summary>
    public static bool SameLine(string a, string b)
    {
        if (TextMatch.IsSameSubtitle(a, b)) return true;
        if (Math.Min(a.Length, b.Length) < 3) return false;
        // Başa konuşmacı adı eklenmiş: "moon here take my radio" / "here take my radio".
        if (a.EndsWith(b, StringComparison.Ordinal) || b.EndsWith(a, StringComparison.Ordinal)) return true;
        // Sona yazı tanıma kırıntısı eklenmiş: "cho shit j" / "cho shit" (fark en çok 3 karakter).
        if (Math.Abs(a.Length - b.Length) <= 3 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)))
            return true;
        // Yazı belirirken bozuk okunan uzun satır: "there s mure ccminq par fire that rpg" / "there s more coming park ...".
        return Math.Min(a.Length, b.Length) >= 16 && TextMatch.IsSimilar(a, b, 0.72);
    }

    private RecentLine? FindRecent(string key, long now)
    {
        recent.RemoveAll(line => now - line.SeenAt > RecentMs && now - line.SpokenAt > RecentMs);
        for (var i = recent.Count - 1; i >= 0; i--)
            if (SameLine(key, recent[i].Key)) return recent[i];
        return null;
    }

    /// <summary>Gösterilen çeviriyi hatırlar (kilit altında).</summary>
    private void Remember(string source, string translation)
    {
        var key = TextMatch.Normalize(source);
        var now = Environment.TickCount64;
        if (FindRecent(key, now) is not { } line)
        {
            line = new RecentLine { Key = key };
            recent.Add(line);
            if (recent.Count > 32) recent.RemoveAt(0);
        }
        line.Translation = translation;
        line.SeenAt = now;
    }

    /// <summary>
    /// Yalnız az önce seslendirilmemiş satırlar seslendirilir. Oyun önceki repliği ekranda tutup yenisini altına
    /// eklediğinde ("A / B", sonra "B / C") B ikinci kez okunmaz; dublaj her yeniden okumada baştan başlamaz ve
    /// yazının gerisinde kalmaz.
    /// </summary>
    private void SpeakOnce(string source)
    {
        if (SpeechReady is not { } speech) return;
        var lines = translator.SpokenLines(source);
        var fresh = new List<Dubbing.SpokenLine>();
        lock (gate)
        {
            var now = Environment.TickCount64;
            foreach (var line in lines)
            {
                var key = TextMatch.Normalize(line.English.Length > 0 ? line.English : line.Turkish);
                var spoken = spokenLines.FindLastIndex(entry => now - entry.At <= RecentMs && SameLine(key, entry.Key));
                if (spoken >= 0) continue;
                spokenLines.Add((key, now));
                fresh.Add(line);
            }
            spokenLines.RemoveAll(entry => now - entry.At > RecentMs);
        }
        if (fresh.Count > 0) speech(fresh);
    }

    /// <summary>Son seslendirilen satırlar (İngilizcesi, zamanı).</summary>
    private readonly List<(string Key, long At)> spokenLines = [];

    private async Task Translate(string source, TextBox? box, int number, int current, string? known = null)
    {
        string translated;
        Task<string?>? refined = null;
        try
        {
            if (known is not null) translated = known;
            else if (translator is IProgressiveTranslator progressive)
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
                // Çeviri olmadı: replik "çevriliyor" diye bekletilmez, yeniden okununca yeniden denenir.
                var failed = TextMatch.Normalize(source);
                recent.RemoveAll(line => line.Translation.Length == 0 && line.Key == failed);
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
            // Gösterilmese de (yerini yenisine bıraktıysa) çeviri hatırlanır; replik "çevriliyor" diye takılı kalmaz.
            Remember(source, translated);
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
            Remember(source, translated);
        }
        PairShown?.Invoke(source, translated);
        // Yeniden okunan replik: kayıtlı çevirisi gösterildi, seslendirilmez.
        if (known is not null) return;
        // Dublaj iyileştirilmiş çeviriyi bekler (varsa); yoksa hızlı çeviri hemen seslendirilir.
        if (refined is null)
        {
            SpeakOnce(source);
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
            if (current == session && better is { Length: > 0 }) Remember(source, better);
        }
        if (changed) PairShown?.Invoke(source, better!);
        SpeakOnce(source);
    }

    /// <summary>Okuma durunca TV'deki son altyazıyı kaldırır.</summary>
    public void Reset()
    {
        lock (gate)
        {
            session++;
            lineOnScreen = null;
            recent.Clear();
            spokenLines.Clear();
            lastReading = Reading.Empty;
            schedule.Reset();
            if (stabilizer.Reset() == StabilizerAction.Clear) sender.Clear();
        }
    }
}
