using MetaVoiceType.Audio;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Sessions;
using MetaVoiceType.Transcription;
using Microsoft.Extensions.Logging.Abstractions;

namespace MetaVoiceType.Tests;

public sealed class AudioAndSessionTests
{
    [Fact]
    public void PcmRoundTripProducesCorrectFrameSizeAndSamples()
    {
        float[] input = [-1f, -0.25f, 0f, 0.25f, 1f];
        byte[] pcm = Pcm16Converter.ToPcm16(input);
        AudioFrame frame = Pcm16Converter.Convert(pcm);
        Assert.Equal(input.Length * 2, pcm.Length);
        Assert.Equal(input.Length, frame.Samples.Length);
        Assert.InRange(frame.Samples[1], -0.251, -0.249);
        Assert.InRange(frame.Peak, 0.999, 1.0);
    }

    [Fact]
    public async Task FinalizingOldSessionDoesNotPreventCreatingNewSession()
    {
        var coordinator = new DecodeCoordinator(NullLogger<DecodeCoordinator>.Instance);
        coordinator.Start();
        var a = new DictationSession("auto", 0, new FakeAsrBackend("alpha"), new FlushSegmenter(new float[1600]));
        a.Accept(Pcm16Converter.Convert(new byte[640]));
        coordinator.Finalize(a, a.Stop(false, false));

        var b = new DictationSession("auto", 1600, new FakeAsrBackend("bravo"), new FlushSegmenter([]));
        Assert.Equal(DictationStatus.Recording, b.Status);
        Assert.NotEqual(a.Id, b.Id);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (a.Status == DictationStatus.Finalizing) await Task.Delay(10, timeout.Token);
        Assert.Equal("alpha", a.FinalText);
        await coordinator.DisposeAsync();
        b.Dispose();
    }

    [Fact]
    public async Task ControlAudioSpanIsRemovedBeforeBackendTranscription()
    {
        var backend = new RecordingAsrBackend();
        using var session = new DictationSession("auto", 10_000, backend, new FlushSegmenter(new float[4_000]));
        session.Accept(Pcm16Converter.Convert(new byte[8_000]));
        IReadOnlyList<DictationSegment> jobs = session.Stop(false, false);
        IReadOnlyList<DictationSegment> replacements = session.MarkControlSpan(11_500, 12_500);
        Assert.Single(replacements);

        var coordinator = new DecodeCoordinator(NullLogger<DecodeCoordinator>.Instance);
        coordinator.Start();
        coordinator.Finalize(session, jobs.Concat(replacements));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (session.Status == DictationStatus.Finalizing) await Task.Delay(10, timeout.Token);

        Assert.Equal([1_500, 1_500], backend.Lengths);
        Assert.Equal("1500 1500", session.FinalText);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public void AcceptedControlPhraseRemovedFromTranscriptBoundaries()
    {
        Assert.Equal("we discussed stop recording yesterday", TranscriptTailCleaner.RemoveAcceptedCommandTail("we discussed stop recording yesterday. stop recording", "stop recording"));
        Assert.Equal("is a phrase", TranscriptTailCleaner.RemoveAcceptedCommandBoundary("stop recording is a phrase", "stop recording"));
        Assert.Equal("ordinary words in the middle stay", TranscriptTailCleaner.RemoveAcceptedCommandBoundary("ordinary words in the middle stay", "stop recording"));
        Assert.Equal("the useful text", TranscriptTailCleaner.RemoveAcceptedCommandBoundary("stop the useful text", "stop recording"));
    }

    private sealed class FakeAsrBackend(string result) : IAsrBackend
    {
        public AsrRuntimeStatus Status { get; } = new("test", "Test", "cpu", "CPU", null, "test", null);
        public string Transcribe(float[] samples) => result;
        public void Dispose() { }
    }

    private sealed class RecordingAsrBackend : IAsrBackend
    {
        public List<int> Lengths { get; } = [];
        public AsrRuntimeStatus Status { get; } = new("test", "Test", "cpu", "CPU", null, "test", null);
        public string Transcribe(float[] samples) { Lengths.Add(samples.Length); return samples.Length.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        public void Dispose() { }
    }

    private sealed class FlushSegmenter(float[] samples) : ISpeechSegmenter
    {
        public IReadOnlyList<SpeechAudioSegment> Accept(ReadOnlySpan<float> input) => [];
        public IReadOnlyList<SpeechAudioSegment> Flush() => samples.Length == 0 ? [] : [new(0, samples)];
        public void Dispose() { }
    }
}
