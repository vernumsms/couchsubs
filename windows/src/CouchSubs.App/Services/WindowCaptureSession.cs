// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using CouchSubs.Core;
using CouchSubs.RemotePlay;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace CouchSubs.App.Services;

/// <summary>
/// Görüntüyü PS5 yerine bilgisayardaki bir pencereden (Xbox uygulamasının "Uzaktan Oynat" penceresi) alır.
/// Windows.Graphics.Capture kullanır: pencere başka pencerelerin arkasında kalsa da okunur (simge durumunda değilse).
/// Pencere bulunamazsa ya da kapanırsa birkaç saniyede bir yeniden aranır.
/// </summary>
public sealed class WindowCaptureSession(string titleContains) : IStreamSession
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(66);
    private static readonly TimeSpan SearchInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FastSearchInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MissingGrace = TimeSpan.FromSeconds(8);

    private readonly Lock gate = new();
    private CancellationTokenSource? loop;
    private Capture? capture;
    private SessionState? lastState;

    public event Action<Frame>? FrameReceived;
    public event Action<SessionState>? StateChanged;

    public void Start()
    {
        lock (gate)
        {
            if (loop is not null) return;
            var cancellation = new CancellationTokenSource();
            loop = cancellation;
            Report(SessionState.Connecting);
            _ = Task.Run(() => RunAsync(cancellation.Token));
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            loop?.Cancel();
            loop = null;
            capture?.Dispose();
            capture = null;
        }
        Report(SessionState.Disconnected(DisconnectKind.Stopped));
    }

    public void ReconnectNow()
    {
        lock (gate)
        {
            capture?.Dispose();
            capture = null;
        }
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken token)
    {
        // Pencere kısa süre kaybolup geri gelirse (Xbox uygulaması oyuna geçerken pencereyi yeniler) bağlantı kopmuş sayılmaz;
        // kopmuş sayılırsa TV'deki altyazı silinir ve çeviri sıfırlanır.
        var missingSince = Stopwatch.GetTimestamp();
        var reportedMissing = false;
        while (!token.IsCancellationRequested)
        {
            Capture? current;
            lock (gate) current = capture;
            if (current is null || current.Closed)
            {
                if (current is not null) Log("pencere kapandı");
                current?.Dispose();
                current = TryOpen();
                lock (gate)
                {
                    if (token.IsCancellationRequested) { current?.Dispose(); return; }
                    capture = current;
                }
            }
            if (current is null || current.Closed)
            {
                if (!reportedMissing && Stopwatch.GetElapsedTime(missingSince) > MissingGrace)
                {
                    reportedMissing = true;
                    Log("pencere bulunamadı");
                    Report(SessionState.Disconnected(DisconnectKind.ConsoleAsleep, "window"));
                }
            }
            else
            {
                missingSince = Stopwatch.GetTimestamp();
                reportedMissing = false;
                if (current.Delivering) Report(SessionState.Streaming);
            }
            try { await Task.Delay(current is { Delivering: true } ? SearchInterval : FastSearchInterval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private Capture? TryOpen()
    {
        var window = FindWindow(titleContains);
        if (window == IntPtr.Zero) return null;
        try
        {
            var opened = new Capture(window, frame => FrameReceived?.Invoke(frame));
            Log($"pencere açıldı: {Describe(window)}");
            return opened;
        }
        catch (Exception e)
        {
            Log($"pencere okunamadı: {Describe(window)} | {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    private static string Describe(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var process);
        GetWindowRect(window, out var rect);
        return $"{ProcessName(process, [])} '{Title(window)}' {rect.Right - rect.Left}x{rect.Bottom - rect.Top}";
    }

    /// <summary>%LOCALAPPDATA%\CouchSubs\pencere.log: pencere bulma/kapanma olayları (Xbox sorunlarını uzaktan anlamak için).</summary>
    private static void Log(string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchSubs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "pencere.log");
            if (System.IO.File.Exists(path) && new FileInfo(path).Length > 512 * 1024) System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Günlük yazılamaması yakalamayı durdurmaz.
        }
    }

    private void Report(SessionState state)
    {
        lock (gate)
        {
            if (state == lastState) return;
            lastState = state;
        }
        StateChanged?.Invoke(state);
    }

    /// <summary>Geliştirme aracı: pencereden bir kare okur, PNG olarak kaydeder. 0 başarı, 1 pencere yok, 2 kare gelmedi.</summary>
    public static async Task<int> CaptureTestAsync(string title, string output)
    {
        if (FindWindow(title) == IntPtr.Zero) return 1;
        var received = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new WindowCaptureSession(title);
        session.FrameReceived += frame => received.TrySetResult(frame);
        session.Start();
        if (await Task.WhenAny(received.Task, Task.Delay(8000)) != received.Task) return 2;
        var frame = received.Task.Result;
        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(frame.Width, frame.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, frame.Pixels, frame.Stride);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        await using var file = System.IO.File.Create(output);
        encoder.Save(file);
        return 0;
    }

    // --- Pencereyi bulma ---

    /// <summary>
    /// Başlığında ya da uygulama (işlem) adında verilen metin geçen, görünür, simge durumunda olmayan en büyük üst düzey
    /// pencere (bu uygulamanınki hariç). Yeni Xbox uygulamasının (XboxPcApp) penceresinin başlığı boş olabildiği için
    /// işlem adına da bakılır; aynı uygulamanın küçük yan pencereleri (arkadaş listesi vb.) en büyüğü seçilerek elenir.
    /// </summary>
    public static IntPtr FindWindow(string titleContains)
    {
        if (string.IsNullOrWhiteSpace(titleContains)) return IntPtr.Zero;
        var wanted = titleContains.Trim();
        var own = (uint)Environment.ProcessId;
        var names = new Dictionary<uint, string>();
        IntPtr found = IntPtr.Zero;
        long foundArea = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window) || IsCloaked(window)) return true;
            GetWindowThreadProcessId(window, out var process);
            if (process == own) return true;
            if (!Title(window).Contains(wanted, StringComparison.OrdinalIgnoreCase)
                && !ProcessName(process, names).Contains(wanted, StringComparison.OrdinalIgnoreCase)) return true;
            if (!GetWindowRect(window, out var rect)) return true;
            long width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width < 320 || height < 180) return true;
            if (width * height > foundArea)
            {
                found = window;
                foundArea = width * height;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string ProcessName(uint process, Dictionary<uint, string> cache)
    {
        if (cache.TryGetValue(process, out var name)) return name;
        try
        {
            using var p = Process.GetProcessById((int)process);
            name = p.ProcessName;
        }
        catch (Exception)
        {
            name = "";
        }
        cache[process] = name;
        return name;
    }

    private static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Açık pencerelerin başlıkları (ayarlarda seçmek için).</summary>
    public static IReadOnlyList<string> WindowTitles()
    {
        var own = (uint)Environment.ProcessId;
        var titles = new List<string>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var process);
            if (process == own) return true;
            var title = Title(window);
            if (title.Length > 0 && !titles.Contains(title)) titles.Add(title);
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    private static string Title(IntPtr window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0) return "";
        var text = new StringBuilder(length + 1);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    // --- Yakalama ---

    private sealed class Capture : IDisposable
    {
        private readonly IDirect3DDevice device;
        private readonly Direct3D11CaptureFramePool pool;
        private readonly GraphicsCaptureSession session;
        private readonly GraphicsCaptureItem item;
        private readonly Action<Frame> deliver;
        private Windows.Graphics.SizeInt32 size;
        private long lastFrameTicks;
        private int busy;
        private volatile bool closed;
        private volatile bool delivering;
        private byte[]? buffer;
        private (int X, int Y, int Width, int Height)? content;
        private long measuredTicks;

        public bool Closed => closed;
        public bool Delivering => delivering;

        public Capture(IntPtr window, Action<Frame> deliver)
        {
            this.deliver = deliver;
            item = CreateItemForWindow(window);
            device = CreateDevice();
            size = item.Size;
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            session = pool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = false; } catch { /* eski Windows */ }
            item.Closed += (_, _) => closed = true;
            pool.FrameArrived += OnFrameArrived;
            session.StartCapture();
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null || closed) return;
            if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
            {
                // Pencere boyutu değişti: havuz yeni boyutla yeniden kurulur; bu kare atlanır.
                size = frame.ContentSize;
                if (size.Width > 0 && size.Height > 0)
                    sender.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                return;
            }
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastFrameTicks), now) < FrameInterval) return;
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            Interlocked.Exchange(ref lastFrameTicks, now);
            try
            {
                using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)
                    .AsTask().GetAwaiter().GetResult();
                var width = bitmap.PixelWidth;
                var height = bitmap.PixelHeight;
                var bytes = width * height * 4;
                if (buffer is null || buffer.Length != bytes) buffer = new byte[bytes];
                bitmap.CopyToBuffer(buffer.AsBuffer());
                // Bantlar iki saniyede bir ölçülür; her karede ölçülse karanlık sahnelerde kırpım oynar ve seçili altyazı bölgesi kayar.
                if (content is null || content.Value.Width > width || content.Value.Height > height
                    || Stopwatch.GetElapsedTime(measuredTicks, now) > TimeSpan.FromSeconds(2))
                {
                    content = ContentBounds(buffer, width, height);
                    measuredTicks = now;
                }
                var area = content.Value;
                if (area.X + area.Width > width || area.Y + area.Height > height) area = (0, 0, width, height);
                deliver(CropToFrame(buffer, width, area));
                delivering = true;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Kare okunamadı: {e.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
            }
        }

        public void Dispose()
        {
            closed = true;
            try { pool.FrameArrived -= OnFrameArrived; } catch { }
            try { session.Dispose(); } catch { }
            try { pool.Dispose(); } catch { }
            try { device.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Görüntünün kenarlarındaki siyah bantlar (ekran oranı tutmadığında) atılır: TV'deki yerleşim oyunun kendi görüntüsüne
    /// göre hesaplandığı için bantlar kalırsa altyazı TV'de kayar.
    /// </summary>
    public static (int X, int Y, int Width, int Height) ContentBounds(byte[] bgra, int width, int height)
    {
        const int dark = 14;
        bool RowDark(int y)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x += 8)
            {
                var i = row + x * 4;
                if (bgra[i] > dark || bgra[i + 1] > dark || bgra[i + 2] > dark) return false;
            }
            return true;
        }
        bool ColumnDark(int x, int top, int bottom)
        {
            for (var y = top; y < bottom; y += 8)
            {
                var i = (y * width + x) * 4;
                if (bgra[i] > dark || bgra[i + 1] > dark || bgra[i + 2] > dark) return false;
            }
            return true;
        }
        var top = 0;
        while (top < height / 3 && RowDark(top)) top++;
        var bottom = height;
        while (bottom > height * 2 / 3 && RowDark(bottom - 1)) bottom--;
        var left = 0;
        while (left < width / 3 && ColumnDark(left, top, bottom)) left++;
        var right = width;
        while (right > width * 2 / 3 && ColumnDark(right - 1, top, bottom)) right--;
        // Tamamen karanlık sahnede (ör. yükleme ekranı) bantlar yanlış ölçülür; o zaman tüm kare kullanılır.
        if (bottom - top < height / 2 || right - left < width / 2) return (0, 0, width, height);
        return (left, top, right - left, bottom - top);
    }

    private static Frame CropToFrame(byte[] bgra, int width, (int X, int Y, int Width, int Height) area)
    {
        var stride = area.Width * 4;
        var pixels = new byte[stride * area.Height];
        for (var y = 0; y < area.Height; y++)
            Buffer.BlockCopy(bgra, ((area.Y + y) * width + area.X) * 4, pixels, y * stride, stride);
        return new Frame(pixels, area.Width, area.Height, stride);
    }

    // --- Windows birlikte çalışma ---

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr window)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var pointer = interop.CreateForWindow(window, ref iid);
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static IDirect3DDevice CreateDevice()
    {
        const int hardware = 1;
        const uint bgraSupport = 0x20;
        var result = D3D11CreateDevice(IntPtr.Zero, hardware, IntPtr.Zero, bgraSupport, IntPtr.Zero, 0, 7, out var d3d, out _, out var context);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        try
        {
            var iid = DxgiDeviceIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally
        {
            Marshal.Release(d3d);
            if (context != IntPtr.Zero) Marshal.Release(context);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
