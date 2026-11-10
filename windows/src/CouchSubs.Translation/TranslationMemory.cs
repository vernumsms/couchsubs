// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using CouchSubs.Core;
using CouchSubs.Core.Dubbing;

namespace CouchSubs.Translation;

/// <summary>
/// Oyuna özel kalıcı çeviri hafızası: Claude'un çevirdiği her satır diske yazılır
/// (%LOCALAPPDATA%\CouchSubs\hafiza\&lt;oyun&gt;.json). Aynı sahne yeniden oynanınca (bölüm baştan, ölüp yeniden doğma)
/// çeviri beklemeden ve ücretsiz gelir. Konuşmacı adları da saklanır; bir sonraki oturumda Claude onları baştan bilir.
/// </summary>
public sealed class TranslationMemory
{
    /// <summary>Bir oyunun hafızasında tutulan en fazla satır (dosya ~5 MB'ı geçmesin).</summary>
    private const int EntryLimit = 20000;
    /// <summary>Değişiklikler en geç bu kadar sonra diske yazılır.</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(5);

    private readonly string _folder;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private string? _game;
    private bool _dirty;
    private Timer? _saveTimer;

    public TranslationMemory(string folder) => _folder = folder;

    /// <summary>Kayıtlı satır sayısı (o an seçili oyun için).</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Kayıtlı konuşmacı adları.</summary>
    public IReadOnlyList<string> Names
    {
        get { lock (_gate) return [.. _names]; }
    }

    /// <summary>Oyun değiştiyse öncekinin hafızası kaydedilir, yenisininki yüklenir. Ad boşsa "genel" hafıza kullanılır.</summary>
    public void Use(string? game)
    {
        var name = FileName(game);
        lock (_gate)
        {
            if (name == _game) return;
            SaveLocked();
            _entries.Clear();
            _names.Clear();
            _game = name;
            try
            {
                var path = PathOf(name);
                if (!File.Exists(path)) return;
                var file = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path), Json);
                if (file is null) return;
                foreach (var entry in file.Lines ?? []) if (entry.Key is { Length: > 0 }) _entries[entry.Key] = entry;
                foreach (var who in file.Names ?? []) _names.Add(who);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                // Bozuk ya da okunamayan dosya: hafıza boş başlar, yeni çeviriler üstüne yazılır.
            }
        }
    }

    /// <summary>Normalleştirilmiş satır anahtarının kayıtlı çevirisi.</summary>
    internal ClaudeTranslator.LineResult? Lookup(string key)
    {
        lock (_gate) return _entries.TryGetValue(key, out var entry) ? entry.ToResult() : null;
    }

    internal void Store(string key, ClaudeTranslator.LineResult result)
    {
        if (key.Length == 0) return;
        lock (_gate)
        {
            if (_game is null) return;
            if (_entries.Count >= EntryLimit && !_entries.ContainsKey(key)) _entries.Remove(_entries.Keys.First());
            _entries[key] = Entry.From(key, result);
            if (result.Speech?.Speaker is { Length: >= 2 } speaker && _names.Count < 256) _names.Add(speaker);
            ScheduleSaveLocked();
        }
    }

    /// <summary>Bekleyen değişiklikleri hemen yazar (uygulama kapanırken).</summary>
    public void Flush()
    {
        lock (_gate) SaveLocked();
    }

    private void ScheduleSaveLocked()
    {
        _dirty = true;
        _saveTimer ??= new Timer(_ => Flush());
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void SaveLocked()
    {
        if (!_dirty || _game is null) return;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(_folder);
            var path = PathOf(_game);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new StoredFile([.. _names], [.. _entries.Values]), Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _dirty = true;
        }
    }

    private string PathOf(string name) => Path.Combine(_folder, name + ".json");

    /// <summary>Oyun adından güvenli dosya adı ("Call of Duty: Black Ops 7" → "call-of-duty-black-ops-7").</summary>
    internal static string FileName(string? game)
    {
        var chars = (game ?? "").Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : c switch
            {
                'ç' => 'c', 'ğ' => 'g', 'ı' => 'i', 'ö' => 'o', 'ş' => 's', 'ü' => 'u',
                _ => '-',
            }).ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return slug.Length == 0 ? "genel" : slug;
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private sealed record StoredFile(List<string>? Names, List<Entry>? Lines);

    internal sealed record Entry(string Key, string Subtitle, bool Interface, string? English = null, string? Spoken = null,
        string? Speaker = null, bool NewTurn = false, SpeechEmotion? Emotion = null, VoiceGender? Gender = null, bool Background = false)
    {
        public static Entry From(string key, ClaudeTranslator.LineResult result) => result.Speech is { } line
            ? new(key, result.Subtitle, result.Interface, line.English, line.Turkish, line.Speaker, line.NewTurn, line.Emotion, line.Gender, line.Background)
            : new(key, result.Subtitle, result.Interface);

        public ClaudeTranslator.LineResult ToResult() => new(Subtitle, Interface, Spoken is null ? null
            : new SpokenLine(English ?? "", Spoken, Speaker, NewTurn, Emotion, Gender, Background));
    }
}
