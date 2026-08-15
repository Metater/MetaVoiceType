using MetaVoiceType.Audio;
using MetaVoiceType.Diagnostics;
using MetaVoiceType.Sessions;
using MetaVoiceType.Storage;
using MetaVoiceType.Transcription;
using Microsoft.Extensions.Logging.Abstractions;

namespace MetaVoiceType.Tests;

public sealed class DiagnosticsAndRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MetaVoiceType.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void StartupOptionsAreStronglyParsed()
    {
        StartupOptions options = StartupOptions.Parse(["--self-test", "--diagnostics", "--force-cpu", "--paste-text", "exact text"]);
        Assert.True(options.SelfTest);
        Assert.True(options.Diagnostics);
        Assert.True(options.ForceCpu);
        Assert.True(options.ExitAfterDiagnostics);
        Assert.Equal("exact text", options.PasteText);
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(["--not-an-option"]));
    }

    [Fact]
    public async Task RecoveryCloseFlushesBeforeCompletionAndAllowsDeletion()
    {
        var paths = new AppPaths(_root);
        await using var writer = new RecoveryWriter(paths, NullLogger<RecoveryWriter>.Instance);
        writer.Start();
        using var session = new DictationSession("auto", 0, new NoOpAsrBackend(), new NoOpSegmenter());
        writer.Enqueue(session, Pcm16Converter.Convert(new byte[640]));

        await writer.CloseAsync(session).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        string audio = Path.Combine(paths.Recovery, session.Id, "audio.pcm");
        Assert.Equal(640, new FileInfo(audio).Length);
        writer.Delete(session.Id);
        Assert.False(Directory.Exists(Path.GetDirectoryName(audio)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class NoOpAsrBackend : IAsrBackend
    {
        public AsrRuntimeStatus Status { get; } = new("test", "Test", "cpu", "CPU", null, "test", null);
        public string Transcribe(float[] samples) => "";
        public void Dispose() { }
    }

    private sealed class NoOpSegmenter : ISpeechSegmenter
    {
        public IReadOnlyList<SpeechAudioSegment> Accept(ReadOnlySpan<float> samples) => [];
        public IReadOnlyList<SpeechAudioSegment> Flush() => [];
        public void Dispose() { }
    }
}
