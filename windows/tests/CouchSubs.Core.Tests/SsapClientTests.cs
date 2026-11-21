// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CouchSubs.Core.LgTv;

namespace CouchSubs.Core.Tests;

/// SSAP konuşan sahte bir TV: register'a client-key ile, isteklere sırayla verilen yanıtlarla döner.
public sealed class FakeLgTv : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    public List<string> ReceivedUris { get; } = [];
    public List<JsonNode?> ReceivedPayloads { get; } = [];
    public Queue<string> ForegroundApps { get; } = new();
    /// true ise register'a yalnız "soru gösterildi" yanıtı verir, onay hiç gelmez.
    public bool NeverApprove { get; set; }
    public int Port { get; }

    public FakeLgTv()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); } catch { return; }
            if (!context.Request.IsWebSocketRequest) { context.Response.StatusCode = 400; context.Response.Close(); continue; }
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            _ = Serve(socket);
        }
    }

    private async Task Serve(WebSocket socket)
    {
        var buffer = new byte[8192];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, stop.Token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                // Gerçek LG TV ikili çerçeveyi reddeder; yalnız metin çerçevesi kabul edilir.
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await Send(socket, new JsonObject { ["type"] = "error", ["id"] = "", ["error"] = "400 binary messages not supported" });
                    continue;
                }
                var message = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!;
                await Handle(socket, message);
            }
        }
        catch { }
    }

    private async Task Handle(WebSocket socket, JsonNode message)
    {
        var id = (string?)message["id"] ?? "";
        if ((string?)message["type"] == "register")
        {
            await Send(socket, new JsonObject { ["type"] = "response", ["id"] = id, ["payload"] = new JsonObject { ["pairingType"] = "PROMPT", ["returnValue"] = true } });
            if (!NeverApprove)
                await Send(socket, new JsonObject { ["type"] = "registered", ["id"] = id, ["payload"] = new JsonObject { ["client-key"] = "fake-key" } });
            return;
        }
        var uri = (string?)message["uri"] ?? "";
        lock (ReceivedUris)
        {
            ReceivedUris.Add(uri);
            ReceivedPayloads.Add(message["payload"]?.DeepClone());
        }
        var payload = new JsonObject { ["returnValue"] = true };
        if (uri.EndsWith("getForegroundAppInfo"))
            payload["appId"] = ForegroundApps.Count == 0 ? "" : ForegroundApps.Dequeue();
        await Send(socket, new JsonObject { ["type"] = "response", ["id"] = id, ["payload"] = payload });
    }

    private static Task Send(WebSocket socket, JsonObject message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    public void Dispose()
    {
        stop.Cancel();
        listener.Close();
    }
}

public class SsapClientTests
{
    private static readonly Dictionary<string, string> Server = new() { ["server"] = "http://1.2.3.4:8787" };

    [Fact]
    public async Task ConnectReturnsClientKeyAndLaunchSendsUri()
    {
        using var tv = new FakeLgTv();
        using var client = new SsapClient([new Uri($"ws://127.0.0.1:{tv.Port}")], null);
        Assert.Equal("fake-key", await client.ConnectAsync());

        await client.LaunchAsync("com.couchsubs.tv", Server);
        Assert.Equal("ssap://system.launcher/launch", tv.ReceivedUris.Last());
        Assert.Equal("com.couchsubs.tv", (string?)tv.ReceivedPayloads.Last()!["id"]);
        Assert.Equal("http://1.2.3.4:8787", (string?)tv.ReceivedPayloads.Last()!["params"]!["server"]);
    }

    [Fact]
    public async Task ForegroundAppId()
    {
        using var tv = new FakeLgTv();
        tv.ForegroundApps.Enqueue("com.webos.app.hdmi2");
        using var client = new SsapClient([new Uri($"ws://127.0.0.1:{tv.Port}")], "fake-key");
        await client.ConnectAsync();
        Assert.Equal("com.webos.app.hdmi2", await client.ForegroundAppIdAsync());
    }

    [Fact]
    public async Task UnansweredPairingPromptTimesOut()
    {
        using var tv = new FakeLgTv { NeverApprove = true };
        using var client = new SsapClient([new Uri($"ws://127.0.0.1:{tv.Port}")], null, TimeSpan.FromSeconds(1));
        var begin = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync());
        Assert.True(DateTime.UtcNow - begin < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FallsBackToNextEndpoint()
    {
        using var tv = new FakeLgTv();
        // İlk adreste kimse dinlemiyor; ikinciye düşmeli.
        using var client = new SsapClient([new Uri("ws://127.0.0.1:1"), new Uri($"ws://127.0.0.1:{tv.Port}")], null);
        Assert.Equal("fake-key", await client.ConnectAsync());
    }
}

public class TvKeeperTests
{
    private sealed class FakeControl(params string[] foreground) : ITvControl
    {
        private readonly Queue<string> foreground = new(foreground);
        public List<(string AppId, IReadOnlyDictionary<string, string> Params)> Launches { get; } = [];
        public bool FailNext { get; set; }

        public Task<string> ForegroundAppIdAsync(CancellationToken cancellationToken = default)
        {
            if (FailNext) { FailNext = false; throw new IOException("bağlantı koptu"); }
            return Task.FromResult(foreground.Count == 0 ? "com.couchsubs.tv" : foreground.Dequeue());
        }

        public Task LaunchAsync(string appId, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken = default)
        {
            Launches.Add((appId, parameters));
            return Task.CompletedTask;
        }

        public int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }

    [Fact]
    public async Task SamsungIsLeftAloneWhileTheAppIsConnected()
    {
        var control = new FakeControl();
        var connected = false;
        var connects = 0;
        var keeper = new TvKeeper(() => { connects++; return Task.FromResult<ITvControl>(control); }, "com.couchsubs.tv",
            new Dictionary<string, string>(), () => connected, TimeSpan.FromMilliseconds(200));
        await keeper.TickAsync(); // uygulama bağlı değil → aç ve bağlantıyı kapat
        Assert.Single(control.Launches);
        Assert.Equal(1, control.Disposals);
        await keeper.TickAsync(); // hemen yeniden açmaya çalışmaz
        Assert.Equal(1, connects);
        connected = true;
        await Task.Delay(250);
        for (var i = 0; i < 5; i++) await keeper.TickAsync(); // uygulama bağlıyken TV'ye dokunulmaz
        Assert.Equal(1, connects);
        connected = false;
        await keeper.TickAsync(); // uygulama kapandı → bir kez daha aç
        Assert.Equal(2, control.Launches.Count);
    }

    [Fact]
    public async Task SamsungRestartsTheAppWhenLaunchDidNotHelp()
    {
        var control = new FakeControl();
        var connected = false;
        var restarts = 0;
        var failRestart = false;
        var keeper = new TvKeeper(() => Task.FromResult<ITvControl>(control), "com.couchsubs.tv",
            new Dictionary<string, string>(), () => connected, TimeSpan.Zero,
            restart: () => { restarts++; return failRestart ? throw new HttpRequestException("TV yok") : Task.CompletedTask; });
        await keeper.TickAsync(); // ilk deneme: uzaktan kumandayla aç
        Assert.Single(control.Launches);
        Assert.Equal(0, restarts);
        await keeper.TickAsync(); // işe yaramadı → kapat ve yeniden başlat
        Assert.Equal(1, restarts);
        Assert.Single(control.Launches);
        failRestart = true;
        await keeper.TickAsync(); // yeniden başlatma olmadı → bağlanma denemesine düşer (TV ağda aranabilsin)
        Assert.Equal(2, control.Launches.Count);
        connected = true;
        await keeper.TickAsync();
        connected = false;
        failRestart = false;
        await keeper.TickAsync(); // bağlandıktan sonra sayaç sıfırlandı → yine önce uzaktan kumanda
        Assert.Equal(3, control.Launches.Count);
        Assert.Equal(2, restarts);
    }

    [Fact]
    public async Task RelaunchesOnlyWhenHdmiInputIsInFront()
    {
        var control = new FakeControl("com.couchsubs.tv", "com.webos.app.hdmi2", "com.couchsubs.tv", "netflix");
        var keeper = new TvKeeper(() => Task.FromResult<ITvControl>(control), "com.couchsubs.tv", new Dictionary<string, string> { ["server"] = "http://1.2.3.4:8787" });
        await keeper.TickAsync(); // bağlanır ve uygulamayı bir kez başlatır
        await keeper.TickAsync(); // önde HDMI girişi → geri getir
        await keeper.TickAsync(); // önde bizimki
        await keeper.TickAsync(); // kullanıcı Netflix'i açtı → dokunma
        Assert.Equal(2, control.Launches.Count);
        Assert.Equal("com.couchsubs.tv", control.Launches[^1].AppId);
        Assert.Equal("http://1.2.3.4:8787", control.Launches[^1].Params["server"]);
    }

    [Fact]
    public async Task ReconnectsWhenTvWasOffOrConnectionDropped()
    {
        var control = new FakeControl();
        var attempts = 0;
        var keeper = new TvKeeper(() =>
        {
            attempts++;
            if (attempts == 1) throw new IOException("TV kapalı");
            return Task.FromResult<ITvControl>(control);
        }, "com.couchsubs.tv", new Dictionary<string, string>());
        await keeper.TickAsync();
        Assert.Empty(control.Launches);
        await keeper.TickAsync(); // TV açıldı → bağlan ve başlat
        Assert.Single(control.Launches);

        control.FailNext = true;  // bağlantı koptu
        await keeper.TickAsync();
        await keeper.TickAsync(); // yeniden bağlanır ve tekrar başlatır
        Assert.Equal(3, attempts);
        Assert.Equal(2, control.Launches.Count);
    }
}
