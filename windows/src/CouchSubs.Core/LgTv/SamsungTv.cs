// SPDX-License-Identifier: AGPL-3.0-only

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CouchSubs.Core.LgTv;

/// <summary>Samsung uzaktan kumanda WebSocket'inin iletileri. Samsung bunu belgelemez; alanlar topluluk kaynaklarından.</summary>
public static class SamsungMessages
{
    public static Uri Url(string host, string? token)
    {
        var name = Convert.ToBase64String(Encoding.UTF8.GetBytes("CouchSubs"));
        var text = $"wss://{host}:8002/api/v2/channels/samsung.remote.control?name={name}";
        if (!string.IsNullOrEmpty(token)) text += $"&token={token}";
        return new Uri(text);
    }

    /// <summary>TV uygulaması parametreleri <c>PAYLOAD</c> içinde alır (bkz. tv-tizen/platform.js).</summary>
    public static string Launch(string appId, IReadOnlyDictionary<string, string> parameters) => new JsonObject
    {
        ["method"] = "ms.channel.emit",
        ["params"] = new JsonObject
        {
            ["event"] = "ed.apps.launch",
            ["to"] = "host",
            ["data"] = new JsonObject
            {
                ["appId"] = appId,
                ["action_type"] = "DEEP_LINK",
                ["metaTag"] = JsonSerializer.Serialize(parameters),
            },
        },
    }.ToJsonString();

    /// <summary>Bağlantı sonucu: onaylandıysa jeton (TV vermediyse boş), başka iletiyse null. Reddedildiyse hata.</summary>
    public static string? ConnectResult(string text)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); } catch (JsonException) { return null; }
        return node?["event"]?.GetValue<string>() switch
        {
            "ms.channel.connect" => node["data"]?["token"]?.GetValue<string>() ?? "",
            "ms.channel.unauthorized" => throw new SsapException("rejected"),
            _ => null,
        };
    }

    /// <summary><c>http://TV:8001/api/v2/</c> yanıtındaki TV adı.</summary>
    public static string? DeviceName(string json)
    {
        try { return JsonNode.Parse(json)?["device"]?["name"]?.GetValue<string>()?.Replace("&quot;", "\""); }
        catch (JsonException) { return null; }
    }
}

/// <summary>Samsung TV ile uzaktan kumanda WebSocket'i üzerinden konuşur. İlk bağlantıda TV izin sorar.</summary>
public sealed class SamsungClient(Uri url, TimeSpan? approvalTimeout = null) : ITvControl
{
    public const string AppId = "CouchSubsT.CouchSubs";

    private ClientWebSocket? socket;

    public SamsungClient(LgTv tv, string? token) : this(SamsungMessages.Url(tv.Host, token)) { }

    /// <summary>Bağlanır ve jetonu döner. Kullanıcı TV'de onay verene kadar bekler.</summary>
    public async Task<string> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var candidate = new ClientWebSocket();
        // TV'nin 8002 portundaki sertifikası kendinden imzalıdır; bu bağlantı yalnız TV'nin adresine gider.
        candidate.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(approvalTimeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await candidate.ConnectAsync(url, timeout.Token);
            var buffer = new byte[8192];
            while (true)
            {
                var text = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await candidate.ReceiveAsync(buffer, timeout.Token);
                    if (result.MessageType == WebSocketMessageType.Close) throw new SsapException("cannot connect");
                    text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);
                if (SamsungMessages.ConnectResult(text.ToString()) is { } token)
                {
                    socket = candidate;
                    return token;
                }
            }
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public async Task LaunchAsync(string appId, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
    {
        if (socket is null) throw new SsapException("cannot connect");
        var data = Encoding.UTF8.GetBytes(SamsungMessages.Launch(appId, parameters));
        await socket.SendAsync(data, WebSocketMessageType.Text, true, cancellationToken);
    }

    /// <summary>Samsung önplandaki uygulamayı söylemez; bağlantı ayakta mı diye bakılır, boş döner.</summary>
    public Task<string> ForegroundAppIdAsync(CancellationToken cancellationToken = default) =>
        socket is { State: WebSocketState.Open } ? Task.FromResult("") : throw new SsapException("cannot connect");

    public void Dispose()
    {
        socket?.Abort();
        socket?.Dispose();
        socket = null;
    }
}

/// <summary>
/// Samsung'un 8001 portundaki uygulama arayüzü. İzin sorusu gerektirmez; uzaktan kumanda bağlantısıyla "aç" demek arka
/// plana düşmüş uygulamayı öne getirmediğinde uygulama burada kapatılıp yeniden başlatılır. Parametresiz başlayan TV
/// uygulaması kayıtlı adresi, olmazsa ağı tarayıp bilgisayarı kendisi bulur.
/// </summary>
public static class SamsungApps
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static string Url(string host, string appId) => $"http://{host}:8001/api/v2/applications/{appId}";

    public static async Task RestartAsync(string host, string appId, CancellationToken cancellationToken = default)
    {
        var url = Url(host, appId);
        (await Http.DeleteAsync(url, cancellationToken)).Dispose(); // kapalıysa da 200 döner
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        using var response = await Http.PostAsync(url, null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Yerel ağdaki Samsung TV'leri SSDP ile bulur.</summary>
public static class SamsungDiscovery
{
    internal const string SearchTarget = "urn:samsung.com:device:RemoteControlReceiver:1";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public static async Task<IReadOnlyList<LgTv>> SearchAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var hosts = await LgDiscovery.CollectResponses(SearchTarget, timeout ?? TimeSpan.FromSeconds(3), cancellationToken);
        var tvs = new List<LgTv>();
        foreach (var host in hosts.Distinct().Order(StringComparer.Ordinal))
        {
            string? name = null;
            try { name = SamsungMessages.DeviceName(await Http.GetStringAsync($"http://{host}:8001/api/v2/", cancellationToken)); }
            catch (Exception) { /* ad okunamazsa genel ad kullanılır */ }
            tvs.Add(new LgTv(name ?? "Samsung TV", host, TvBrand.Samsung));
        }
        if (tvs.Count > 0) return tvs;

        // SSDP yanıtı gelmediyse (bazı modemler ve TV'ler çok noktaya yayını geçirmez) ağdaki adresler tek tek denenir:
        // Samsung TV 8001 portunda kendi adını veren bir arayüz sunar.
        foreach (var host in await LocalNetwork.HostsWithOpenPortAsync(8001, TimeSpan.FromMilliseconds(700), cancellationToken))
        {
            try
            {
                if (SamsungMessages.DeviceName(await Http.GetStringAsync($"http://{host}:8001/api/v2/", cancellationToken)) is { } name)
                    tvs.Add(new LgTv(name, host, TvBrand.Samsung));
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
        }
        return tvs;
    }
}
