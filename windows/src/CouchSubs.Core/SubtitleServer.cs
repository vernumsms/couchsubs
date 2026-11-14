// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CouchSubs.Core;

/// <summary>TV'de çevirinin oyundaki İngilizce altyazıya göre yeri.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubtitlePlacement>))]
public enum SubtitlePlacement
{
    /// <summary>İngilizce altyazının üstüne, onu örterek.</summary>
    Cover,
    /// <summary>İngilizce altyazının hemen üstünde.</summary>
    Above,
    /// <summary>İngilizce altyazının hemen altında.</summary>
    Below,
    /// <summary>Ekranda sabit bir yükseklikte (<see cref="SubtitleStyle.BottomPercent"/>).</summary>
    Fixed,
}

/// <summary>TV'de altyazının görünümü. Eski sürümün kaydettiği ayarlarda olmayan alanlar varsayılanla dolar.</summary>
/// <param name="FontSize">36 (küçük), 46 (orta), 58 (büyük), 72 (çok büyük)</param>
/// <param name="BottomPercent">Sabit yerleşimde altyazının ekranın altından uzaklığı, yüzde.</param>
/// <param name="Background">Yazının arkasındaki siyah zeminin koyuluğu, yüzde (0: zemin yok).</param>
public sealed record SubtitleStyle(
    int FontSize = 46,
    int BottomPercent = 7,
    SubtitlePlacement Placement = SubtitlePlacement.Cover,
    int Background = 72,
    bool Bold = false,
    SubtitleTheme Theme = SubtitleTheme.Modern);

/// <summary>Altyazının görünümü (TV'de).</summary>
public enum SubtitleTheme
{
    /// <summary>Yumuşak köşeli şerit, ince çerçeve ve gölge; konuşmacı adı sarı.</summary>
    Modern,
    /// <summary>Eski düz, yarı saydam kutu.</summary>
    Classic,
}

/// <summary>Altyazıları TV'ye ileten taraf (gerçekte <see cref="SubtitleServer"/>).</summary>
public interface ISubtitleSender
{
    /// <param name="box">Oyundaki İngilizce altyazının ekrandaki yeri (kenarlara oranla); TV çeviriyi buna göre yerleştirir.</param>
    void SendSubtitle(string text, int durationMs = 15000, TextBox? box = null);
    void Clear();
}

/// <summary>
/// TV uygulamasının bağlandığı yerel Server-Sent Events sunucusu.
/// <c>GET /events</c> akışında <c>subtitle</c>, <c>clear</c> ve <c>style</c> olaylarını yayınlar.
/// </summary>
public sealed class SubtitleServer(int port = 8787) : ISubtitleSender, IDisposable
{
    private sealed class Client(TcpClient tcp, bool viewer = false)
    {
        public TcpClient Tcp { get; } = tcp;
        /// <summary>OBS katmanı gibi TV olmayan izleyici: olayları alır ama "TV bağlı" sayısına katılmaz.</summary>
        public bool Viewer { get; } = viewer;
        public Stream Stream { get; } = tcp.GetStream();
        /// <summary>Aynı bağlantıya iki yazma iç içe geçmesin.</summary>
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Türkçe harfler ı gibi kaçışlarla değil olduğu gibi gönderilir.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object gate = new();
    private readonly List<Client> clients = [];
    private TcpListener? listener;
    private Timer? pingTimer;
    private SubtitleStyle? lastStyle;
    /// <summary>GET ile verilen dosyalar: dublaj sesleri ve seslendirme modelinin ses örnekleri.</summary>
    private readonly Dictionary<string, (byte[] Data, string Type)> files = new(StringComparer.Ordinal);
    private readonly Queue<string> speechPaths = new();
    private int speechNumber;

    /// <summary>Bağlı TV sayısı değişince çağrılır (iş parçacığı havuzunda).</summary>
    public event Action<int>? ClientsChanged;

    public int ConnectedClients
    {
        get { lock (gate) return TvCount(); }
    }

    private int TvCount() => clients.Count(c => !c.Viewer);

    /// <summary>OBS'de "Tarayıcı kaynağı" olarak eklenen altyazı katmanı (<c>GET /obs</c>; Mac ile aynı dosya).</summary>
    public static byte[] ObsOverlay { get; } = LoadObsOverlay();

    private static byte[] LoadObsOverlay()
    {
        using var stream = typeof(SubtitleServer).Assembly.GetManifestResourceStream("obs-overlay.html");
        if (stream is null) return [];
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Sunucuyu başlatır ve dinlenen portu döner (<c>port: 0</c> ise rastgele seçilen port).</summary>
    public Task<int> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        this.listener = listener;
        _ = AcceptLoop(listener);
        pingTimer = new Timer(_ => Broadcast(": ping\n\n"), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        return Task.FromResult(((IPEndPoint)listener.LocalEndpoint).Port);
    }

    public void Stop()
    {
        pingTimer?.Dispose();
        pingTimer = null;
        listener?.Stop();
        listener = null;
        List<Client> closing;
        lock (gate)
        {
            closing = [.. clients];
            clients.Clear();
        }
        foreach (var client in closing) client.Tcp.Dispose();
    }

    public void Dispose() => Stop();

    public void SendSubtitle(string text, int durationMs = 15000, TextBox? box = null) => SendSubtitle(text, durationMs, box, false);

    /// <param name="paragraphs">true ise metin satır satır, sola yaslı gösterilir (görev paneli gibi başlık ve maddeli yazılar).</param>
    public void SendSubtitle(string text, int durationMs, TextBox? box, bool paragraphs)
    {
        var json = new SortedDictionary<string, object>
        {
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["duration"] = durationMs,
            ["text"] = text,
        };
        if (paragraphs) json["paragraphs"] = true;
        if (box is { } b) json["box"] = BoxJson(b);
        Broadcast(Frame("subtitle", json));
    }

    /// <summary>Dört basamağa yuvarlanır (decimal: JSON'da 0.8 olarak yazılsın).</summary>
    /// Bölme sondaki sıfırları atar (0.8750 → 0.875).
    private static decimal Round(double value) =>
        Math.Round((decimal)Math.Clamp(value, 0, 1), 4) / 1.000000000000000000000000000000000m;

    public void Clear() => Broadcast(Frame("clear", new SortedDictionary<string, object>()));

    /// <summary>
    /// "Ekranı çevir" sonuçları: <c>screen</c> olayı her çeviriyi yazının yeri (<c>box</c>), tipik satır yüksekliği
    /// (<c>line</c>, ekrana oranla) ve çok parçalıysa <c>paragraphs</c> ile taşır. TV öncekinin yerine bunları koyar.
    /// </summary>
    public void SendScreen(IReadOnlyList<ScreenTranslation.Item> items, int durationMs)
    {
        var list = items.Select(item =>
        {
            var json = new SortedDictionary<string, object>
            {
                ["box"] = BoxJson(item.Box),
                ["line"] = Round(item.LineHeight),
                ["text"] = item.Translation,
            };
            if (item.Paragraphs) json["paragraphs"] = true;
            return json;
        }).ToList();
        Broadcast(Frame("screen", new SortedDictionary<string, object> { ["duration"] = durationMs, ["items"] = list }));
    }

    /// <summary>TV'deki "ekranı çevir" çevirilerini kaldırır (altyazıya dokunmaz).</summary>
    public void ClearScreen() => Broadcast(Frame("screen-clear", new SortedDictionary<string, object>()));

    private static SortedDictionary<string, object> BoxJson(TextBox b)
    {
        var json = new SortedDictionary<string, object>
        {
            ["bottom"] = Round(b.Bottom), ["left"] = Round(b.Left), ["right"] = Round(b.Right), ["top"] = Round(b.Top),
        };
        // Oyundaki satırın yüksekliği: TV çeviriyi aynı yazı boyunda gösterir.
        if (b.Line > 0) json["line"] = Round(b.Line);
        return json;
    }

    /// <summary>Dosyayı <paramref name="path"/> adresinden verir ("/voices/male_1.wav").</summary>
    public void Publish(byte[] data, string path, string type = "audio/wav")
    {
        lock (gate) files[path] = (data, type);
    }

    /// <summary>
    /// Dublaj sesini TV'ye gönderir: ses <c>/speech/&lt;n&gt;.wav</c> adresinden verilir, <c>speech</c> olayı adresi
    /// bildirir. Son 16 ses tutulur.
    /// </summary>
    public void SendSpeech(byte[] wav, int durationMs)
    {
        string path;
        lock (gate)
        {
            path = $"/speech/{++speechNumber}.wav";
            files[path] = (wav, "audio/wav");
            speechPaths.Enqueue(path);
            while (speechPaths.Count > 16) files.Remove(speechPaths.Dequeue());
        }
        Broadcast(Frame("speech", new SortedDictionary<string, object> { ["duration"] = durationMs, ["url"] = path }));
    }

    /// <summary>TV'de çalan dublajı durdurur.</summary>
    public void StopSpeech() => Broadcast(Frame("speech-stop", new SortedDictionary<string, object>()));

    public void SendStyle(SubtitleStyle style)
    {
        lock (gate) lastStyle = style;
        Broadcast(StyleFrame(style));
    }

    // --- Bağlantılar ---

    private async Task AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = Serve(tcp);
        }
    }

    private async Task Serve(TcpClient tcp)
    {
        try
        {
            EnableKeepAlive(tcp.Client);
            var stream = tcp.GetStream();
            var requestLine = await ReadRequestLine(stream);
            var parts = requestLine?.Split(' ') ?? [];
            var method = parts.Length > 0 ? parts[0] : "";
            var target = parts.Length > 1 ? parts[1] : "";
            var path = target.Split('?')[0];

            if (method == "OPTIONS") await RespondAndClose(tcp, "204 No Content");
            else if (method == "GET" && path == "/events") await OpenStream(tcp, target.Contains("viewer=obs", StringComparison.Ordinal));
            else if (method == "GET" && path == "/obs") await RespondAndClose(tcp, ObsOverlay, "text/html; charset=utf-8");
            // TV uygulaması ağdaki bilgisayarı bu yanıtla tanır.
            else if (method == "GET" && path == "/hello") await RespondAndClose(tcp, "200 OK", "{\"app\":\"couchsubs\"}");
            else if (method == "GET" && FindFile(path) is { } file) await RespondAndClose(tcp, file.Data, file.Type);
            else await RespondAndClose(tcp, "404 Not Found");
        }
        catch
        {
            tcp.Dispose();
        }
    }

    /// <summary>Başlıkların sonuna (boş satır) kadar okur, ilk satırı döner. 64 KB'yi aşan istek kapatılır.</summary>
    private static async Task<string?> ReadRequestLine(Stream stream)
    {
        var buffer = new byte[8192];
        using var head = new MemoryStream();
        while (head.Length < 65536)
        {
            var count = await stream.ReadAsync(buffer);
            if (count == 0) return null;
            head.Write(buffer, 0, count);
            var text = Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
        }
        return null;
    }

    private (byte[] Data, string Type)? FindFile(string path)
    {
        lock (gate) return files.TryGetValue(path, out var file) ? file : null;
    }

    private static async Task RespondAndClose(TcpClient tcp, byte[] data, string type)
    {
        var head = $"HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nContent-Type: {type}\r\nCache-Control: no-cache\r\n" +
                   $"Content-Length: {data.Length}\r\nConnection: close\r\n\r\n";
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(data);
        tcp.Dispose();
    }

    private static async Task RespondAndClose(TcpClient tcp, string status, string body = "")
    {
        var head = $"HTTP/1.1 {status}\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
        tcp.Dispose();
    }

    private async Task OpenStream(TcpClient tcp, bool viewer = false)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: text/event-stream; charset=utf-8\r\n")
            .Append("Cache-Control: no-cache\r\n")
            .Append("Connection: keep-alive\r\n")
            .Append("Access-Control-Allow-Origin: *\r\n\r\n")
            .Append("retry: 2000\n\n");
        var client = new Client(tcp, viewer);
        int count;
        lock (gate)
        {
            if (lastStyle is { } style) head.Append(StyleFrame(style));
            clients.Add(client);
            count = TvCount();
        }
        await Write(client, head.ToString());
        ClientsChanged?.Invoke(count);
        await WatchForClose(client);
    }

    /// <summary>İstemci bağlantıyı kapattığında listeden çıkarır.</summary>
    private async Task WatchForClose(Client client)
    {
        var buffer = new byte[1024];
        try
        {
            while (await client.Stream.ReadAsync(buffer) > 0) { }
        }
        catch { }
        Remove(client);
    }

    private void Remove(Client client)
    {
        int count;
        lock (gate)
        {
            if (!clients.Remove(client)) return;
            count = TvCount();
        }
        client.Tcp.Dispose();
        ClientsChanged?.Invoke(count);
    }

    /// <summary>Gönderim hatası alan bağlantı kopmuş sayılır ve listeden çıkarılır.</summary>
    private void Broadcast(string frame)
    {
        List<Client> targets;
        lock (gate) targets = [.. clients];
        foreach (var client in targets)
            _ = Write(client, frame).ContinueWith(t => Remove(client), TaskContinuationOptions.OnlyOnFaulted);
    }

    private static async Task Write(Client client, string text)
    {
        await client.WriteLock.WaitAsync();
        try
        {
            await client.Stream.WriteAsync(Encoding.UTF8.GetBytes(text));
            await client.Stream.FlushAsync();
        }
        finally
        {
            client.WriteLock.Release();
        }
    }

    /// <summary>Sessizce kaybolan TV'ler (fişten çekme, Wi-Fi kopması) TCP canlılık denetimiyle fark edilir.</summary>
    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException) { }
    }

    private static string StyleFrame(SubtitleStyle style) =>
        Frame("style", new SortedDictionary<string, object>
        {
            ["background"] = Math.Clamp(style.Background, 0, 100),
            ["bold"] = style.Bold,
            ["bottomPercent"] = style.BottomPercent,
            ["fontSize"] = style.FontSize,
            ["placement"] = style.Placement.ToString().ToLowerInvariant(),
            ["theme"] = style.Theme.ToString().ToLowerInvariant(),
        });

    private static string Frame(string name, SortedDictionary<string, object> json) =>
        $"event: {name}\ndata: {JsonSerializer.Serialize(json, JsonOptions)}\n\n";
}
