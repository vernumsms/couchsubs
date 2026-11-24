// SPDX-License-Identifier: AGPL-3.0-only

namespace CouchSubs.Core.LgTv;

/// <summary>
/// TV uygulamasını açık tutar: TV kendi HDMI ya da yayın girişine geçerse (SimpLink/CEC) uygulamayı
/// yeniden öne getirir. TV kapalıysa ya da bağlantı koparsa bir sonraki kontrolde yeniden bağlanır.
/// </summary>
/// <param name="appConnected">
/// Verilirse (Samsung) TV'ye yalnız TV uygulaması bilgisayara bağlı değilken ve en çok <paramref name="relaunchInterval"/>'da
/// bir dokunulur: uygulama açılır, bağlantı hemen kapatılır. Samsung önplandaki uygulamayı söylemediği için sürekli açık
/// "uzaktan kumanda" bağlantısı gereksizdi; yeniden kurulan bağlantı TV'de bildirim çıkarıp PS5 kolunu düşürüyordu (#5).
/// </param>
/// <param name="restart">
/// Verilirse (Samsung): açma denemesi işe yaramadıysa sonraki denemelerde TV uygulaması kapatılıp yeniden başlatılır.
/// TV bekleme moduna girince ya da kaynak değişince uygulama arka planda çalışır kalır; uzaktan kumanda bağlantısıyla
/// "aç" demek onu öne getirmez ve bilgisayara yeniden bağlanmaz.
/// </param>
/// <param name="wake">
/// Verilirse (Samsung): TV hiç cevap vermiyorsa (bekleme modu) ağdan uyandırma paketi gönderilir. Yalnız TV uygulaması bu
/// oturumda henüz hiç bağlanmamışken (çeviri yeni başladı) ya da kullanıcı "TV'yi yeniden bağla" dediğinde: kullanıcı
/// oyun sırasında TV'yi bilerek kapattıysa yeniden açılmaz.
/// </param>
public sealed class TvKeeper(Func<Task<ITvControl>> connect, string appId, IReadOnlyDictionary<string, string> parameters,
                             Func<bool>? appConnected = null, TimeSpan? relaunchInterval = null,
                             Func<Task>? restart = null, Func<Task>? wake = null) : IDisposable
{
    private readonly SemaphoreSlim tickLock = new(1, 1);
    private ITvControl? control;
    private CancellationTokenSource? loop;
    private DateTime? lastLaunch;
    private DateTime? lastWake;
    private int missedLaunches;
    private bool everConnected;
    private bool wakeRequested;

    /// <summary>TV'nin son denemedeki durumu (yalnız <c>appConnected</c> verildiyse izlenir).</summary>
    public TvReach Reach { get; private set; }

    /// <summary>
    /// Kullanıcı istedi: bekleme süresi atlanır, uygulama hemen yeniden başlatılır, TV cevap vermezse uyandırılır.
    /// </summary>
    public void RepairNow()
    {
        lastLaunch = null;
        lastWake = null;
        missedLaunches = Math.Max(missedLaunches, 1);
        wakeRequested = true;
        _ = Task.Run(TickAsync);
    }

    /// <summary>Kullanıcının bilerek açtığı uygulamalara (Netflix vb.) dokunulmaz; yalnız giriş ve yayın ekranları.</summary>
    public static bool IsInputOrLiveTv(string appId) =>
        appId.StartsWith("com.webos.app.hdmi", StringComparison.Ordinal)
        || appId.StartsWith("com.webos.app.externalinput", StringComparison.Ordinal)
        || appId == "com.webos.app.livetv";

    /// <summary>Bir kontrol. Hata fırlatmaz; bağlantı sorununda bir sonraki kontrolde yeniden dener.</summary>
    public async Task TickAsync()
    {
        await tickLock.WaitAsync();
        try
        {
            if (appConnected is not null)
            {
                await LaunchIfAppIsAwayAsync(appConnected());
                return;
            }
            if (control is null)
            {
                var fresh = await connect();
                control = fresh;
                await fresh.LaunchAsync(appId, parameters);
                return;
            }
            var current = await control.ForegroundAppIdAsync();
            if (IsInputOrLiveTv(current)) await control.LaunchAsync(appId, parameters);
        }
        catch
        {
            control?.Dispose();
            control = null;
        }
        finally
        {
            tickLock.Release();
        }
    }

    private async Task LaunchIfAppIsAwayAsync(bool connected)
    {
        if (connected)
        {
            missedLaunches = 0;
            everConnected = true;
            wakeRequested = false;
            Reach = TvReach.Connected;
            return;
        }
        if (lastLaunch is { } last && DateTime.UtcNow - last < (relaunchInterval ?? TimeSpan.FromSeconds(30))) return;
        lastLaunch = DateTime.UtcNow;
        missedLaunches++;
        try
        {
            if (restart is not null && missedLaunches >= 2)
            {
                try
                {
                    await restart();
                    Reach = TvReach.AppAway;
                    return;
                }
                catch (Exception)
                {
                    // Kayıtlı adreste TV yok: aşağıdaki bağlantı denemesi gerekirse ağda yeniden arar.
                }
            }
            using var fresh = await connect();
            await fresh.LaunchAsync(appId, parameters);
            Reach = TvReach.AppAway;
        }
        catch (Exception)
        {
            // TV kapalı ya da ulaşılamıyor; bir sonraki denemede yeniden.
            Reach = TvReach.Unreachable;
            await WakeIfWantedAsync();
        }
    }

    private async Task WakeIfWantedAsync()
    {
        if (wake is null || (everConnected && !wakeRequested)) return;
        if (lastWake is { } last && DateTime.UtcNow - last < TimeSpan.FromSeconds(60)) return;
        lastWake = DateTime.UtcNow;
        try { await wake(); }
        catch (Exception) { /* TV'nin ağ adresi (MAC) bilinmiyor ya da ağ yok */ }
    }

    public void Start(TimeSpan? interval = null)
    {
        loop?.Cancel();
        var cancellation = new CancellationTokenSource();
        loop = cancellation;
        var delay = interval ?? TimeSpan.FromSeconds(5);
        _ = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                await TickAsync();
                try { await Task.Delay(delay, cancellation.Token); } catch (OperationCanceledException) { }
            }
        });
    }

    /// <summary>Bilgisayar uykudan uyandığında: bağlantıyı yenile.</summary>
    public void Reconnect()
    {
        control?.Dispose();
        control = null;
        lastLaunch = null;
        missedLaunches = 0;
        Reach = TvReach.Unknown;
    }

    public void Stop()
    {
        loop?.Cancel();
        loop = null;
        control?.Dispose();
        control = null;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// TV'nin durumu. AppAway: TV ağda cevap veriyor, uygulama yeniden başlatıldı. Unreachable: TV ağda hiç cevap vermiyor
/// (kapalı, bekleme modunda ya da ağ bağlantısı takılmış).
/// </summary>
public enum TvReach { Unknown, Connected, AppAway, Unreachable }
