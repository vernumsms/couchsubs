// SPDX-License-Identifier: AGPL-3.0-only

using System.Buffers.Binary;
using System.Text;

namespace CouchSubs.Core.Dubbing;

/// <summary>Tek kanallı ses, örnekler -1…1 arası (Mac: SpeechAudio.swift).</summary>
public sealed class SpeechAudio(float[] samples, int sampleRate)
{
    public float[] Samples { get; } = samples;
    public int SampleRate { get; } = sampleRate;
    public double Duration => (double)Samples.Length / SampleRate;

    /// <summary>
    /// 16 bit tam sayı ya da 32 bit kayan noktalı PCM WAV okur. Akış hâlinde yazılmış dosyada (boyut alanları 0 ya da
    /// en büyük değer) veri dosyanın sonuna kadar sürer. Çok kanallıysa ilk kanal alınır.
    /// </summary>
    public static SpeechAudio FromWav(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes[..4]) != "RIFF" || Encoding.ASCII.GetString(bytes[8..12]) != "WAVE")
            throw new InvalidDataException("WAV değil");
        int format = 0, channels = 1, rate = 24000, bits = 16;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes.Slice(offset, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            var body = offset + 8;
            if (id == "fmt " && body + 16 <= bytes.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body, 2));
                channels = Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 2, 2)));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 14, 2));
                // WAVE_FORMAT_EXTENSIBLE: asıl biçim alt türün ilk iki baytında.
                if (format == 0xFFFE && body + 26 <= bytes.Length) format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(body + 24, 2));
            }
            else if (id == "data")
            {
                var end = size == 0 || size == uint.MaxValue || body + (long)size > bytes.Length ? bytes.Length : body + (int)size;
                var frame = channels * bits / 8;
                if (frame <= 0) throw new InvalidDataException("Desteklenmeyen WAV");
                var samples = new float[(end - body) / frame];
                for (var i = 0; i < samples.Length; i++)
                {
                    var at = bytes.Slice(body + i * frame);
                    samples[i] = (format, bits) switch
                    {
                        (1, 16) => BinaryPrimitives.ReadInt16LittleEndian(at) / 32768f,
                        (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(at),
                        _ => throw new InvalidDataException("Desteklenmeyen WAV"),
                    };
                }
                return new SpeechAudio(samples, rate);
            }
            offset = body + (int)Math.Min(size, int.MaxValue - body - 1) + (int)(size & 1);
        }
        throw new InvalidDataException("WAV verisi yok");
    }

    /// <summary>16 bit PCM WAV.</summary>
    /// <summary>
    /// Arka plan konuşmasını bu sesin üstüne karıştırır: <paramref name="gain"/> ile kısılır, <paramref name="offset"/>
    /// saniye sonra başlar. Örnekleme hızı farklıysa arka plan atlanır (aynı motorun sesleri hep aynı hızdadır).
    /// </summary>
    public SpeechAudio MixedWith(SpeechAudio background, float gain = 0.45f, double offset = 0.15)
    {
        if (background.SampleRate != SampleRate || background.Samples.Length == 0) return this;
        var start = (int)(offset * SampleRate);
        var mixed = new float[Math.Max(Samples.Length, start + background.Samples.Length)];
        Samples.CopyTo(mixed, 0);
        for (var i = 0; i < background.Samples.Length; i++)
            mixed[start + i] = Math.Clamp(mixed[start + i] + (background.Samples[i] * gain), -1f, 1f);
        return new SpeechAudio(mixed, SampleRate);
    }

    /// <summary>Sesi kısar (arka plan konuşması tek başına çalınacaksa).</summary>
    public SpeechAudio Scaled(float gain) => new([.. Samples.Select(sample => sample * gain)], SampleRate);

    public byte[] ToWav()
    {
        var payload = Samples.Length * 2;
        var bytes = new byte[44 + payload];
        var span = bytes.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + payload);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], payload);
        for (var i = 0; i < Samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], (short)(Math.Clamp(Samples[i], -1f, 1f) * 32767));
        return bytes;
    }

    /// <summary>
    /// Akıcılık: model virgül ve ünlemden sonra 200–400 ms susuyor. Cümle içindeki her sessizlik en çok
    /// <paramref name="maxPause"/>'a, baştaki <paramref name="lead"/>'e, sondaki <paramref name="tail"/>'e iner;
    /// kesilen yerler <paramref name="fade"/> ile yumuşatılır (Mac ile aynı).
    /// </summary>
    public SpeechAudio Tightened(double maxPause = 0.2, double lead = 0.03, double tail = 0.08, double fade = 0.01)
    {
        var hop = Math.Max(1, SampleRate / 100);
        var frames = Samples.Length / hop;
        if (frames <= 2) return this;
        var levels = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (var i = frame * hop; i < (frame + 1) * hop; i++) sum += Samples[i] * Samples[i];
            levels[frame] = MathF.Sqrt(sum / hop);
        }
        var threshold = levels.Max() * 0.03f;
        if (threshold <= 0) return this;
        var first = Array.FindIndex(levels, level => level >= threshold);
        var last = Array.FindLastIndex(levels, level => level >= threshold);

        var maxGap = (int)Math.Round(maxPause * 100);
        var keep = new List<(int Start, int End)>();
        var segmentStart = first;
        var at = first;
        while (at <= last)
        {
            if (levels[at] >= threshold) { at++; continue; }
            var end = at;
            while (end <= last && levels[end] < threshold) end++;
            var gap = end - at;
            if (gap > maxGap)
            {
                // Sessizliğin ortası kesilir; iki yanda konuşmanın sönümü kalır.
                var cut = gap - maxGap;
                var middle = at + (gap - cut) / 2;
                keep.Add((segmentStart * hop, middle * hop));
                segmentStart = middle + cut;
            }
            at = end;
        }
        keep.Add((segmentStart * hop, Math.Min(Samples.Length, (last + 1) * hop)));
        var leadSamples = (int)(lead * SampleRate);
        var tailSamples = (int)(tail * SampleRate);
        keep[0] = (Math.Max(0, keep[0].Start - leadSamples), keep[0].End);
        keep[^1] = (keep[^1].Start, Math.Min(Samples.Length, keep[^1].End + tailSamples));

        var fadeSamples = Math.Max(1, (int)(fade * SampleRate));
        var output = new List<float>(Samples.Length);
        foreach (var (start, end) in keep)
        {
            if (end <= start) continue;
            var piece = Samples[start..end];
            var count = Math.Min(fadeSamples, piece.Length / 2);
            for (var i = 0; i < count; i++)
            {
                var gain = (float)i / fadeSamples;
                piece[i] *= gain;
                piece[^(i + 1)] *= gain;
            }
            output.AddRange(piece);
        }
        return new SpeechAudio([.. output], SampleRate);
    }
}
