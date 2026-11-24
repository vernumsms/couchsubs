// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core.Dubbing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CouchSubs.Translation;

/// <summary>
/// Konuşanı sesinden tanıyan model: WeSpeaker ResNet34 (VoxCeleb, CC-BY-4.0, ~26 MB). İlk kullanımda indirilir;
/// ses hiçbir yere gönderilmez. Karakter seslerini ayırmak için (bkz. <see cref="CharacterVoices"/>).
/// </summary>
public sealed class SpeakerEmbeddingModel : ISpeakerModel
{
    public const string ModelFile = "voxceleb_resnet34_LM.onnx";
    private const string Revision = "f0c48c298fd835726c27956a5d617bad7115627e";

    private static readonly IReadOnlyList<ModelFile> Files =
    [
        new(ModelFile, new Uri($"https://huggingface.co/Wespeaker/wespeaker-voxceleb-resnet34-LM/resolve/{Revision}/{ModelFile}"),
            26530309, "7bb2f06e9df17cdf1ef14ee8a15ab08ed28e8d0ef5054ee135741560df2ec068"),
    ];

    private readonly ModelStore store;

    public SpeakerEmbeddingModel(string installDirectory) => store = new ModelStore(installDirectory, Files);

    public bool IsInstalled => store.IsInstalled;

    public Task DownloadAsync(CancellationToken cancellationToken) => store.DownloadAsync(null, cancellationToken);

    public ISpeakerEmbedder CreateEmbedder() => new SpeakerEmbedder(store.PathOf(ModelFile));
}

/// <summary>16 kHz konuşmadan 256 boyutlu, birim uzunlukta ses imzası. Aynı anda tek parça işlenir.</summary>
public sealed class SpeakerEmbedder : ISpeakerEmbedder
{
    private readonly InferenceSession session;
    private readonly string input;
    private readonly object gate = new();

    public SpeakerEmbedder(string modelPath)
    {
        var options = new SessionOptions
        {
            // Oyun akışı, yazı tanıma ve dublajla yarışmasın: tek çekirdek yeter (2 sn ses ~20 ms).
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        session = new InferenceSession(modelPath, options);
        input = session.InputMetadata.Keys.First();
    }

    public float[]? Embed(ReadOnlySpan<float> samples16k)
    {
        var features = Fbank.Compute(samples16k, out var frames);
        if (frames < 50) return null; // yarım saniyeden kısa
        var tensor = new DenseTensor<float>(features, [1, frames, Fbank.Bins]);
        float[] embedding;
        lock (gate)
        {
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(input, tensor)]);
            embedding = results.First().AsEnumerable<float>().ToArray();
        }
        return CharacterVoices.Normalized(embedding);
    }

    public void Dispose()
    {
        lock (gate) session.Dispose();
    }
}
