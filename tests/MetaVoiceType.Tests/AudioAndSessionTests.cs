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
        var aChannel = new FakeAsrChannel("alpha");
        var a = new DictationSession("auto", aChannel);
        a.Accept(Pcm16Converter.Convert(new byte[640]));
        a.Stop(false, false);
        coordinator.Finalize(a);

        var b = new DictationSession("auto", new FakeAsrChannel("bravo"));
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
    public void AcceptedControlPhraseRemovedOnlyFromTail()
    {
        Assert.Equal("we discussed stop recording yesterday", TranscriptTailCleaner.RemoveAcceptedCommandTail("we discussed stop recording yesterday. stop recording", "stop recording"));
        Assert.Equal("stop recording is a phrase", TranscriptTailCleaner.RemoveAcceptedCommandTail("stop recording is a phrase", "stop recording"));
    }

    private sealed class FakeAsrChannel(string result) : IAsrChannel
    {
        private bool _finished;
        private bool _decoded;
        public string CurrentText => _decoded ? result : "";
        public void Accept(float[] samples) { }
        public void Finish() => _finished = true;
        public bool IsReady() => _finished && !_decoded;
        public string Decode() { _decoded = true; return result; }
        public void Dispose() { }
    }
}
