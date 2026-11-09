// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;
using CouchSubs.Core.Dubbing;
using Xunit;

namespace CouchSubs.Shell.Tests;

public class DubberTests
{
    private sealed class FakeSynth : ISpeechSynthesizer
    {
        public List<(string Text, Uri Voice)> Calls { get; } = [];
        public TimeSpan Delay { get; init; }
        /// <summary>Verilirse her seslendirme bu açılana kadar bekler: testler süreye değil sıraya dayanır.</summary>
        public TaskCompletionSource? Gate { get; set; }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<SpeechAudio> SynthesizeAsync(string text, Uri voice, CancellationToken cancellationToken = default)
        {
            lock (Calls) Calls.Add((text, voice));
            if (Gate is { } gate) await gate.Task;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            // 0,05 sn ses: testler hızlı biter.
            return new SpeechAudio([.. Enumerable.Range(0, 1200).Select(i => MathF.Sin(i * 0.1f) * 0.5f)], 24000);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeModel(FakeSynth synth) : IDubbingModel
    {
        public bool IsInstalled => true;
        public long DownloadBytes => 0;
        public Task DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;
        public byte[] VoiceFile(DubbingVoice voice) => [1, 2, 3];
        public ISpeechSynthesizer CreateSynthesizer() => synth;
    }

    private sealed class FakeAudio : IAudioOutput
    {
        public int Played;
        public int Stopped;
        public void Play(byte[] wav) => Interlocked.Increment(ref Played);
        public void Stop() => Interlocked.Increment(ref Stopped);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Koşul zamanında sağlanmadı");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task LinesAreVoicedInOrderWithEmotionAndTheCharactersVoice()
    {
        var synth = new FakeSynth();
        var audio = new FakeAudio();
        using var server = new SubtitleServer(0);
        VoiceCast? saved = null;
        var dubber = new Dubber(new FakeModel(synth), audio, () => server, () => 8787, action => action())
        {
            Output = DubbingOutput.Computer,
        };
        dubber.CastChanged += cast => saved = cast;
        dubber.Speak([new SpokenLine("Get back!", "Geri çekil!", "KRATOS"), new SpokenLine("Okay.", "Tamam.", "Freya")]);
        await WaitUntil(() => audio.Played == 2);

        Assert.Equal("[kızgın] geri çekil!", synth.Calls[0].Text);
        Assert.Equal("tamam.", synth.Calls[1].Text);
        Assert.StartsWith("http://127.0.0.1:8787/voices/male_", synth.Calls[0].Voice.AbsoluteUri);
        Assert.StartsWith("http://127.0.0.1:8787/voices/female_", synth.Calls[1].Voice.AbsoluteUri);
        Assert.NotNull(saved);
        Assert.Equal(2, saved!.Assignments.Count);
        await dubber.DisposeAsync();
    }

    [Fact]
    public async Task OldLinesAreDroppedWhenSpeechFallsBehind()
    {
        var synth = new FakeSynth();
        var audio = new FakeAudio();
        using var server = new SubtitleServer(0);
        var dubber = new Dubber(new FakeModel(synth), audio, () => server, () => 8787, action => action()) { Output = DubbingOutput.Computer };
        dubber.Speak([.. Enumerable.Range(1, 6).Select(i => new SpokenLine($"Line {i}.", $"Satır {i}."))]);
        await WaitUntil(() => audio.Played == Dubber.Backlog);
        await Task.Delay(200);
        Assert.Equal(Dubber.Backlog, audio.Played);
        Assert.Equal("satır dört.", synth.Calls[0].Text);
        await dubber.DisposeAsync();
    }

    /// Yeni altyazı geldiğinde eski altyazının bekleyen cümleleri seslendirilmez; dublaj geride kalmaz.
    [Fact]
    public async Task NewSubtitleDropsPendingLinesOfOlderOnes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synth = new FakeSynth { Gate = gate };
        var audio = new FakeAudio();
        using var server = new SubtitleServer(0);
        var dubber = new Dubber(new FakeModel(synth), audio, () => server, () => 8787, action => action()) { Output = DubbingOutput.Computer };
        dubber.Speak([new SpokenLine("One.", "Bir."), new SpokenLine("Two.", "İki."), new SpokenLine("Three.", "Üç.")]);
        await WaitUntil(() => { lock (synth.Calls) return synth.Calls.Count == 1; });
        dubber.Speak([new SpokenLine("Next.", "Sonraki.")]);
        gate.SetResult();
        await WaitUntil(() => synth.Calls.Any(call => call.Text == "sonraki."));
        lock (synth.Calls) Assert.DoesNotContain(synth.Calls, call => call.Text is "iki." or "üç.");
        await dubber.DisposeAsync();
    }

    /// <summary>
    /// Dublaj yazının gerisinde kalmaz ama sesi de kesmez: yeni altyazı gelince henüz çalınmamış eski cümle atlanır,
    /// çalan ses durdurulmaz (hızlı sahnelerde her satırda kesilen ses anlaşılmıyordu).
    /// </summary>
    [Fact]
    public async Task NewSubtitleSkipsTheOldLineWithoutCuttingAudio()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synth = new FakeSynth { Gate = gate };
        var audio = new FakeAudio();
        using var server = new SubtitleServer(0);
        var dubber = new Dubber(new FakeModel(synth), audio, () => server, () => 8787, action => action()) { Output = DubbingOutput.Computer };
        dubber.Speak([new SpokenLine("One.", "Bir.")]);
        await WaitUntil(() => { lock (synth.Calls) return synth.Calls.Count == 1; });
        dubber.Speak([new SpokenLine("Next.", "Sonraki.")]);
        Assert.Equal(0, audio.Stopped);
        gate.SetResult();
        await WaitUntil(() => audio.Played == 1);
        await Task.Delay(300);
        Assert.Equal(1, audio.Played);
        lock (synth.Calls) Assert.Contains(synth.Calls, call => call.Text == "sonraki.");
        await dubber.DisposeAsync();
    }

    [Fact]
    public async Task PreviewAlwaysPlaysOnTheComputer()
    {
        var synth = new FakeSynth();
        var audio = new FakeAudio();
        using var server = new SubtitleServer(0);
        var dubber = new Dubber(new FakeModel(synth), audio, () => server, () => 8787, action => action()) { Output = DubbingOutput.Tv };
        dubber.Preview(DubbingVoice.Female2);
        await WaitUntil(() => audio.Played == 1);
        Assert.EndsWith("/voices/female_2.wav", synth.Calls[0].Voice.AbsoluteUri);
        await dubber.DisposeAsync();
    }
}

public class AppVersionTests
{
    [Theory]
    [InlineData("0.1.0-beta.10", "0.1.0-beta.9", true)]
    [InlineData("0.1.0", "0.1.0-beta.12", true)]
    [InlineData("0.2.0-beta.1", "0.1.0", true)]
    [InlineData("v0.1.0-beta.9", "0.1.0-beta.9", false)]
    [InlineData("0.1.0-beta.9", "0.1.0-beta.10", false)]
    [InlineData("0.1.0-beta.9", "0.1.0", false)]
    public void PreReleaseNumbersCompareAsNumbers(string candidate, string current, bool newer) =>
        Assert.Equal(newer, AppVersion.IsNewer(candidate, current));
}
