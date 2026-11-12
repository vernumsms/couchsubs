// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using CouchSubs.Core;

namespace CouchSubs.RemotePlay;

/// <summary>Bağlantı koptuğunda yeniden deneme aralıkları: 2, 4, 8, 16, en çok 30 sn.</summary>
internal sealed class ReconnectPolicy
{
    private int attempt;

    public int NextDelay()
    {
        var delay = Math.Min(30, 2 << attempt);
        attempt = Math.Min(attempt + 1, 5);
        return delay;
    }

    public void Reset() => attempt = 0;
}

internal static class DisconnectReasons
{
    /// <summary>Yerel kütüphanenin CS_QUIT_* değerini kullanıcıya anlatılabilecek nedene çevirir.</summary>
    public static DisconnectReason FromNative(int quit, string? text) => quit switch
    {
        NativeMethods.QuitStopped => new(DisconnectKind.Stopped),
        NativeMethods.QuitInUse => new(DisconnectKind.InUse),
        NativeMethods.QuitRefused => new(DisconnectKind.Refused),
        NativeMethods.QuitNetwork => new(DisconnectKind.Network),
        _ => new(DisconnectKind.Other, text),
    };
}

/// <summary>Sırayla çalışan tek iş parçacıklı kuyruk (Mac'teki seri DispatchQueue karşılığı).</summary>
internal sealed class SerialQueue : IDisposable
{
    private readonly Channel<Action> channel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread thread;

    public SerialQueue(string name)
    {
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.Start();
    }

    public void Post(Action action) => channel.Writer.TryWrite(action);

    public void PostAfter(TimeSpan delay, Action action) =>
        _ = Task.Delay(delay).ContinueWith(_ => Post(action), TaskScheduler.Default);

    public bool IsCurrent => Thread.CurrentThread == thread;

    private void Run()
    {
        var reader = channel.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var action))
            {
                try { action(); }
                catch (Exception error) { RemotePlayLog.Write(0, $"session queue: {error}"); }
            }
        }
    }

    public void Dispose() => channel.Writer.TryComplete();
}

/// <summary>PS5'e (ikinci hesapla) Remote Play bağlantısı kurar ve görüntüyü kare kare verir.
/// Ses kapalıdır, kol girdisi gönderilmez. Bağlantı koparsa kendiliğinden yeniden bağlanır.</summary>
public sealed class StreamSession : IStreamSession, IGameAudioSource, IConsoleRelocator
{
    /// <inheritdoc />
    public event Action<RegisteredConsole>? ConsoleMoved;

    /// <summary>Kayıtlı adreste yanıt yoksa tüm ağ en sık bu aralıkla taranır (PS5 tamamen kapalıysa boşuna taramamak için).</summary>
    internal static readonly TimeSpan RelocateInterval = TimeSpan.FromSeconds(30);
    /// <summary>Yalnız kuyrukta okunup yazılır.</summary>
    private DateTime lastRelocate = DateTime.MinValue;

    /// <inheritdoc />
    public bool ListenToAudio { get; set; }
    /// <inheritdoc />
    public event Action<float[], int>? AudioReceived;

    /// <summary>Saniyede en çok bu kadar kare BGRA'ya çevrilir (Mac'teki gibi).</summary>
    internal const int MaxFps = 20;

    /// <summary>Çözülmüş kare. Oturumun kare iş parçacığında çağrılır; işleyici meşgulken gelen kareler atlanır.</summary>
    public event Action<Frame>? FrameReceived;
    /// <summary>Oturum kuyruğunun iş parçacığında çağrılır.</summary>
    public event Action<SessionState>? StateChanged;

    /// <summary>Yalnız kuyrukta değiştirilir (PS5 yeni adreste bulununca).</summary>
    private RegisteredConsole console;
    private readonly byte[] accountId;
    private readonly SerialQueue queue = new("CouchSubs stream session");
    private readonly FrameDelivery delivery;
    private readonly ReconnectPolicy policy = new();
    /// <summary>Yalnız kuyrukta okunup yazılır.</summary>
    private SessionBox? box;

    /// <summary>Durdurma isteği ve kuşak numarası; her başlat/durdur yeni bir kuşak açar ve eski
    /// bloklar (yeniden deneme, eski oturumun kapanma olayı) kendini iptal eder.</summary>
    private readonly Lock stateLock = new();
    private bool running;
    private int generation;
    private bool disposed;

    public StreamSession(RegisteredConsole console, byte[] accountId)
    {
        if (accountId.Length != 8) throw new ArgumentException("PSN account id must be 8 bytes", nameof(accountId));
        this.console = console;
        this.accountId = (byte[])accountId.Clone();
        delivery = new FrameDelivery(frame => FrameReceived?.Invoke(frame));
    }

    public void Start()
    {
        var current = Bump(running: true);
        queue.Post(() =>
        {
            policy.Reset();
            Connect(current);
        });
    }

    /// <summary>Bloklamaz: oturum arka planda kapatılır.</summary>
    public void Stop()
    {
        Bump(running: false);
        queue.Post(CloseBox);
    }

    /// <summary>Bilgisayar uykudan uyandığında ya da ağ değiştiğinde uygulama tarafından çağrılır.</summary>
    public void ReconnectNow()
    {
        if (!IsRunning) return;
        var current = Bump(running: true);
        queue.Post(() =>
        {
            CloseBox();
            policy.Reset();
            Connect(current);
        });
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            if (disposed) return;
            disposed = true;
        }
        Stop();
        queue.Post(() =>
        {
            queue.Dispose();
            delivery.Dispose();
        });
    }

    // Durum

    private bool IsRunning
    {
        get { lock (stateLock) return running; }
    }

    private int Bump(bool running)
    {
        lock (stateLock)
        {
            this.running = running;
            return ++generation;
        }
    }

    private bool IsCurrent(int value)
    {
        lock (stateLock) return running && generation == value;
    }

    // Bağlantı (hepsi kuyrukta)

    private void Connect(int current)
    {
        if (!IsCurrent(current) || box is not null) return;
        Emit(SessionState.Connecting);

        // PS5 uyuyorsa uyandır; uyanmayı kuyruğu bloklamadan, aralıklı kontrollerle bekle.
        var info = ConsoleDiscovery.Discover(console.Host, TimeSpan.FromSeconds(2)).FirstOrDefault();
        if (info is null && Relocate(current) is { } found) info = found;
        if (!IsCurrent(current)) return;
        if (info is { IsAwake: false })
        {
            try { ConsoleDiscovery.Wake(console); }
            catch (Exception error) { RemotePlayLog.Write(1, $"wake: {error.Message}"); }
            WaitForWake(current, DateTime.UtcNow.AddSeconds(20));
            return;
        }
        OpenSession(current);
    }

    /// <summary>Kayıtlı adreste PS5 yanıt vermediğinde tüm yerel ağı tarar; kayıtlı PS5 başka adreste
    /// bulunursa adresi günceller ve ConsoleMoved ile bildirir. Dinlenme modundaki PS5 de aramaya yanıt verir.</summary>
    private ConsoleInfo? Relocate(int current)
    {
        if (DateTime.UtcNow - lastRelocate < RelocateInterval) return null;
        lastRelocate = DateTime.UtcNow;
        IReadOnlyList<ConsoleInfo> all;
        try { all = ConsoleDiscovery.Discover(null, TimeSpan.FromSeconds(3)); }
        catch (Exception error)
        {
            RemotePlayLog.Write(1, $"PS5 aranamadı: {error.Message}");
            return null;
        }
        if (!IsCurrent(current) || PickMoved(console, all) is not { } found) return null;
        RemotePlayLog.Write(2, $"PS5 yeni adreste bulundu: {console.Host} -> {found.Host}");
        console = console.WithHost(found.Host);
        try { ConsoleMoved?.Invoke(console); }
        catch (Exception error) { RemotePlayLog.Write(0, $"ConsoleMoved handler: {error}"); }
        return found;
    }

    /// <summary>Taramada bulunanlardan kayıtlı PS5'i seçer: önce aynı ad; ağda tek PS5 varsa (ad değişmiş olabilir) o.
    /// Kayıt anahtarı başka bir PS5'te geçmeyeceği için yanlış seçim en kötü bağlantı reddiyle sonuçlanır.</summary>
    internal static ConsoleInfo? PickMoved(RegisteredConsole console, IReadOnlyList<ConsoleInfo> found)
    {
        var others = found.Where(c => c.Host != console.Host).ToList();
        var named = others.Where(c => string.Equals(c.Name, console.Name, StringComparison.Ordinal)).ToList();
        if (named.Count == 1) return named[0];
        return named.Count == 0 && others.Count == 1 ? others[0] : null;
    }

    private void WaitForWake(int current, DateTime deadline)
    {
        queue.PostAfter(TimeSpan.FromSeconds(2), () =>
        {
            if (!IsCurrent(current)) return;
            if (ConsoleDiscovery.Discover(console.Host, TimeSpan.FromSeconds(2)).Any(c => c.IsAwake))
                OpenSession(current);
            else if (DateTime.UtcNow < deadline)
                WaitForWake(current, deadline);
            else
                ScheduleReconnect(current, new DisconnectReason(DisconnectKind.ConsoleAsleep));
        });
    }

    private void OpenSession(int current)
    {
        if (!IsCurrent(current) || box is not null) return;
        var started = SessionBox.Start(this, current, console, accountId, ListenToAudio, out var failure);
        if (started is null)
        {
            ScheduleReconnect(current, new DisconnectReason(DisconnectKind.Other, failure ?? "session could not be started"));
            return;
        }
        box = started;
    }

    /// <summary>Geçerli oturumu kapatır. Kapatma join içerdiği için kuyruğu bekletmemek adına arka planda yapılır.</summary>
    private void CloseBox()
    {
        if (box is not { } closing) return;
        box = null;
        Task.Run(closing.TearDown);
    }

    /// <summary>Yerel kütüphanenin iş parçacığından çağrılır.</summary>
    private void Handle(int eventType, int quitReason, string? text, int eventGeneration)
    {
        switch (eventType)
        {
            case NativeMethods.EventConnected:
                queue.Post(() =>
                {
                    if (box?.Generation != eventGeneration) return;
                    policy.Reset();
                    Emit(SessionState.Streaming);
                });
                break;
            case NativeMethods.EventLoginPin:
                // Kol girdisi göndermediğimiz için şifre giremeyiz; yeniden denemek de aynı yere çıkar.
                queue.Post(() =>
                {
                    if (box?.Generation != eventGeneration) return;
                    Bump(running: false);
                    CloseBox();
                    Emit(SessionState.Disconnected(DisconnectKind.LoginPasscode));
                });
                break;
            case NativeMethods.EventQuit:
                var reason = DisconnectReasons.FromNative(quitReason, text);
                RemotePlayLog.Write(1, $"Oturum kapandı: neden {quitReason} ({reason.Kind}) {text}");
                queue.Post(() =>
                {
                    // Eski bir oturumun (ör. ReconnectNow ile kapatılanın) kapanma olayı yeni oturumu etkilemez.
                    if (box?.Generation != eventGeneration) return;
                    CloseBox();
                    ScheduleReconnect(eventGeneration, reason);
                });
                break;
        }
    }

    private void ScheduleReconnect(int current, DisconnectReason reason)
    {
        Emit(new SessionState(SessionPhase.Disconnected, reason));
        if (!IsCurrent(current)) return;
        var delay = policy.NextDelay();
        queue.PostAfter(TimeSpan.FromSeconds(delay), () => Connect(current));
    }

    private void Emit(SessionState state)
    {
        try { StateChanged?.Invoke(state); }
        catch (Exception error) { RemotePlayLog.Write(0, $"StateChanged handler: {error}"); }
    }

    /// <summary>Tek bir yerel oturumun sahibi. Geri çağrılar bu kutuya gelir; kutu (ve GCHandle'ı) oturum
    /// durdurulup iş parçacıkları beklenene kadar yaşar, böylece geri çağrı bırakılmış nesneye düşmez.</summary>
    private sealed class SessionBox
    {
        public int Generation { get; }
        private readonly StreamSession owner;
        private readonly Lock tearLock = new();
        private GCHandle handle;
        private nint native;
        private string? lastError;

        private SessionBox(StreamSession owner, int generation)
        {
            this.owner = owner;
            Generation = generation;
            handle = GCHandle.Alloc(this);
        }

        public static unsafe SessionBox? Start(StreamSession owner, int generation, RegisteredConsole console, byte[] accountId,
            bool listen, out string? failure)
        {
            var box = new SessionBox(owner, generation);
            failure = null;
            try
            {
                NativeMethods.EnsureInitialized();
                var registKey = NativeMethods.Padded(console.RegistKey, 0x10);
                var morning = NativeMethods.Padded(console.Morning, 0x10);
                delegate* unmanaged[Cdecl]<byte*, int, int, int, nint, void> frame = &OnFrame;
                delegate* unmanaged[Cdecl]<int, int, byte*, nint, void> eventCallback = &OnEvent;
                delegate* unmanaged[Cdecl]<int, byte*, nint, void> log = &OnLog;
                delegate* unmanaged[Cdecl]<float*, int, int, nint, void> audio = &OnAudio;
                fixed (byte* key = registKey)
                fixed (byte* morningPointer = morning)
                fixed (byte* account = accountId)
                    box.native = listen
                        ? NativeMethods.cs_session_start_with_audio(console.Host, key, morningPointer, account, MaxFps,
                                                                    (nint)frame, (nint)audio, (nint)eventCallback, (nint)log,
                                                                    GCHandle.ToIntPtr(box.handle))
                        : NativeMethods.cs_session_start(console.Host, key, morningPointer, account, MaxFps,
                                                         (nint)frame, (nint)eventCallback, (nint)log,
                                                         GCHandle.ToIntPtr(box.handle));
            }
            catch (Exception error)
            {
                box.lastError = error.Message;
            }
            if (box.native != 0) return box;
            // Başlamamış oturumun beklenecek iş parçacığı yok; yalnız tutamak bırakılır.
            failure = box.lastError;
            box.handle.Free();
            return null;
        }

        /// <summary>Oturumu durdurur ve iş parçacıklarını bekler. Yerel geri çağrıdan çağrılmamalı.</summary>
        public void TearDown()
        {
            lock (tearLock)
            {
                if (native == 0) return;
                NativeMethods.cs_session_stop(native);
                native = 0;
                handle.Free();
            }
        }

        private static SessionBox? From(nint user) => user == 0 ? null : GCHandle.FromIntPtr(user).Target as SessionBox;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe void OnFrame(byte* bgra, int width, int height, int stride, nint user)
        {
            try
            {
                if (From(user) is { } box && bgra != null && width > 0 && height > 0)
                    box.owner.delivery.Offer((nint)bgra, width, height, stride);
            }
            catch (Exception error) { RemotePlayLog.Write(0, $"frame: {error.Message}"); }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe void OnAudio(float* samples, int count, int rate, nint user)
        {
            try
            {
                if (From(user) is { } box && samples != null && count > 0 && box.owner.AudioReceived is { } handler)
                    handler(new ReadOnlySpan<float>(samples, count).ToArray(), rate);
            }
            catch (Exception error) { RemotePlayLog.Write(0, $"audio: {error.Message}"); }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe void OnEvent(int eventType, int quitReason, byte* reasonText, nint user)
        {
            try
            {
                if (From(user) is { } box)
                    box.owner.Handle(eventType, quitReason, NativeMethods.Utf8(reasonText), box.Generation);
            }
            catch (Exception error) { RemotePlayLog.Write(0, $"event: {error.Message}"); }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static unsafe void OnLog(int level, byte* message, nint user)
        {
            var text = NativeMethods.Utf8(message);
            // Oturum başlatılamazsa nedeni son hata satırıdır (cs_session_start aynı iş parçacığında yazar).
            if (level == 0 && text is not null && From(user) is { } box) box.lastError = text;
            RemotePlayLog.Write(level, text);
        }
    }
}

/// <summary>Kareleri yerel iş parçacığından alıp kendi iş parçacığında dağıtır. İşleyici meşgulken
/// gelen kareler kopyalanmadan atlanır: ağ iş parçacığı okumayı beklemez, gereksiz bellek ayrılmaz.</summary>
internal sealed class FrameDelivery : IDisposable
{
    private readonly Action<Frame> deliver;
    // Monitor.Wait/Pulse gerektiği için Lock değil düz nesne.
    private readonly object gate = new();
    private readonly Thread thread;
    private Frame? pending;
    private bool busy;
    private bool closed;

    public FrameDelivery(Action<Frame> deliver)
    {
        this.deliver = deliver;
        thread = new Thread(Run) { IsBackground = true, Name = "CouchSubs frames" };
        thread.Start();
    }

    /// <summary>Tampon yalnız bu çağrı süresince geçerlidir.</summary>
    public void Offer(nint bgra, int width, int height, int stride)
    {
        lock (gate)
        {
            if (closed || busy || pending is not null) return;
        }
        var frame = Frame.Copy(bgra, width, height, stride);
        lock (gate)
        {
            if (closed || pending is not null) return;
            pending = frame;
            Monitor.Pulse(gate);
        }
    }

    private void Run()
    {
        while (true)
        {
            Frame frame;
            lock (gate)
            {
                while (pending is null && !closed) Monitor.Wait(gate);
                if (closed) return;
                frame = pending!;
                pending = null;
                busy = true;
            }
            try { deliver(frame); }
            catch (Exception error) { RemotePlayLog.Write(0, $"FrameReceived handler: {error}"); }
            lock (gate) busy = false;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            closed = true;
            pending = null;
            Monitor.Pulse(gate);
        }
    }
}
