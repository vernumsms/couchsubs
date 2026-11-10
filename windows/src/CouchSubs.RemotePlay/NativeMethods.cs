// SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CouchSubs.RemotePlay;

/// <summary>couchsubs_native.dll (windows/native/couchsubs_native.h) çağrıları. Geri çağrılar
/// <c>UnmanagedCallersOnly</c> durağan işlevlerdir; nesneye <c>GCHandle</c> ile ulaşılır.</summary>
internal static unsafe partial class NativeMethods
{
    internal const string Library = "couchsubs_native";

    internal const int EventConnected = 0;
    internal const int EventQuit = 1;
    internal const int EventLoginPin = 2;

    internal const int QuitStopped = 0;
    internal const int QuitInUse = 1;
    internal const int QuitRefused = 2;
    internal const int QuitNetwork = 3;
    internal const int QuitOther = 4;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RegisteredConsoleNative
    {
        public fixed byte Nickname[0x20];
        public fixed byte RegistKey[0x10];
        public fixed byte Morning[0x10];
    }

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int cs_init();

    /// <param name="callback">void (*)(const char *host, const char *name, int awake, void *user)</param>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int cs_discover(nint* targets, int targetCount, int timeoutMs, nint callback, nint user);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int cs_wake(string host, ulong credential);

    /// <param name="log">void (*)(int level, const char *message, void *user)</param>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int cs_register(string host, byte* accountId, uint pin, RegisteredConsoleNative* output,
                                            uint* reason, nint log, nint user);

    /// <param name="frame">void (*)(const uint8_t *bgra, int width, int height, int stride, void *user)</param>
    /// <param name="eventCallback">void (*)(int event, int quit_reason, const char *reason_text, void *user)</param>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint cs_session_start(string host, byte* registKey, byte* morning, byte* accountId, int maxFps,
                                                  nint frame, nint eventCallback, nint log, nint user);

    /// <param name="audio">void (*)(const float *samples, int count, int rate, void *user); 0 ise ses istenmez.</param>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint cs_session_start_with_audio(string host, byte* registKey, byte* morning, byte* accountId, int maxFps,
                                                             nint frame, nint audio, nint eventCallback, nint log, nint user);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void cs_session_stop(nint session);

    private static readonly Lock InitLock = new();
    private static bool initialized;

    /// <summary>Ağ yığını ilk kullanımda bir kez hazırlanır. Kütüphane yoksa DllNotFoundException.</summary>
    internal static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (initialized) return;
            var result = cs_init();
            if (result != 0) throw new InvalidOperationException($"couchsubs_native could not be initialized ({result})");
            initialized = true;
        }
    }

    /// <summary>Kütüphane bu makinede yüklenebiliyor mu (Mac'te ve DLL'siz derlemelerde hayır).</summary>
    internal static bool IsAvailable
    {
        get
        {
            if (!NativeLibrary.TryLoad(Library, typeof(NativeMethods).Assembly, null, out var handle)) return false;
            NativeLibrary.Free(handle);
            return true;
        }
    }

    internal static string? Utf8(byte* text) => text == null ? null : Marshal.PtrToStringUTF8((nint)text);

    /// <summary>C dizisini sıfırdan önceki kısmıyla dizgeye çevirir.</summary>
    internal static string FixedString(byte* bytes, int length)
    {
        var end = 0;
        while (end < length && bytes[end] != 0) end++;
        return System.Text.Encoding.UTF8.GetString(bytes, end);
    }

    /// <summary>Baytları sabit uzunlukta, sıfırla doldurulmuş diziye kopyalar (fazlası kesilir).</summary>
    internal static byte[] Padded(byte[] data, int length)
    {
        var result = new byte[length];
        Array.Copy(data, result, Math.Min(data.Length, length));
        return result;
    }

    internal static string LevelName(int level) => level switch { 0 => "error", 1 => "warning", _ => "info" };
}

/// <summary>libchiaki'nin günlük satırları (hata, uyarı, bilgi). Tanı için; kayıt ve oturum sırasında gelir.</summary>
public static class RemotePlayLog
{
    /// <summary>Yerel kütüphanenin iş parçacığında çağrılır; hızlı dönmeli.</summary>
    public static event Action<string>? MessageLogged;

    internal static void Write(int level, string? message)
    {
        if (message is null) return;
        try { MessageLogged?.Invoke($"[chiaki {NativeMethods.LevelName(level)}] {message}"); }
        catch { /* günlük dinleyicisinin hatası yerel iş parçacığını düşürmemeli */ }
    }
}
