// SPDX-License-Identifier: AGPL-3.0-only

using System.Text;

namespace CouchSubs.Translation;

/// <summary>
/// Yapay zekâ çevirisinin günlüğü (%LOCALAPPDATA%\CouchSubs\ceviri.log): her satırın İngilizcesi, Claude'un Türkçesi,
/// yanıt süresi ve hatalar. Çeviri kalitesini ve gecikmeyi gerçek oyun verisiyle incelemek için; yalnız bu
/// bilgisayarda durur, hiçbir yere gönderilmez. 2 MB'ı geçince eski günlük ceviri.old.log olur.
/// </summary>
public sealed class TranslationLog(string path)
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();

    public void Write(string kind, long milliseconds, string source, string? translation)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" | ")
            .Append(kind).Append(" | ")
            .Append(milliseconds).Append(" ms | ")
            .Append(Flat(source));
        if (translation is not null) line.Append(" => ").Append(Flat(translation));
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
                File.AppendAllText(path, line.Append(Environment.NewLine).ToString(), Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Satır sonları günlükte " / " olur; her kayıt tek satırdır.</summary>
    private static string Flat(string text) => text.ReplaceLineEndings(" / ");
}
