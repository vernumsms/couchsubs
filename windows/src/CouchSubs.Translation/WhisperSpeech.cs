// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;
using Whisper.net;

namespace CouchSubs.Translation;

/// <summary>
/// Oyun sesini dinlemek için Whisper (whisper.cpp) modeli: small.en, 5 bit nicemlenmiş (~190 MB), bilgisayarda çalışır.
/// İlk açılışta indirilir; ses hiçbir yere gönderilmez.
/// </summary>
public sealed class WhisperSpeechModel : IGameSpeechModel
{
    public const string ModelFile = "ggml-small.en-q5_1.bin";
    private const string Revision = "5359861c739e955e79d9a303bcbc70fb988958b1";

    private static readonly IReadOnlyList<ModelFile> Files =
    [
        new(ModelFile, new Uri($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{Revision}/{ModelFile}"),
            190098681, "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30"),
    ];

    private readonly ModelStore store;

    public WhisperSpeechModel(string installDirectory) => store = new ModelStore(installDirectory, Files);

    public bool IsInstalled => store.IsInstalled;
    public long DownloadBytes => store.TotalBytes;

    public Task DownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken) => store.DownloadAsync(progress, cancellationToken);

    public ISpeechRecognizer CreateRecognizer() => new WhisperRecognizer(store.PathOf(ModelFile));
}

/// <summary>Whisper ile İngilizce konuşmayı yazıya çevirir. Aynı anda tek parça işlenir.</summary>
public sealed class WhisperRecognizer : ISpeechRecognizer
{
    private readonly WhisperFactory factory;
    private readonly WhisperProcessor processor;
    private readonly SemaphoreSlim gate = new(1, 1);

    public WhisperRecognizer(string modelPath)
    {
        factory = WhisperFactory.FromPath(modelPath);
        processor = factory.CreateBuilder()
            .WithLanguage("en")
            // Oyun PS5'te çalışıyor; bilgisayarda yazı tanıma ve dublaj var. Çekirdeklerin yarısı yeter.
            .WithThreads(Math.Max(2, Environment.ProcessorCount / 2))
            // Whisper normalde her parçayı 30 sn'ye tamamlayıp işler; parçalar en çok 8 sn olduğu için ~10 sn'lik
            // bağlam yeter. Denemede (Ryzen 5600X) 5,6 sn'lik cümle 4,6 sn yerine 1,5 sn'de, aynı doğrulukla çözüldü.
            .WithAudioContextSize(512)
            // Parçalar birbirinden bağımsız: önceki parçanın metni uydurma tekrara yol açmasın.
            .WithNoContext()
            .WithNoSpeechThreshold(0.6f)
            .Build();
    }

    public async Task<string?> TranscribeAsync(float[] samples, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var text = new System.Text.StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                text.Append(segment.Text).Append(' ');
            }
            return SpeechSegmenter.Clean(text.ToString());
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        processor.Dispose();
        factory.Dispose();
        gate.Dispose();
    }
}
