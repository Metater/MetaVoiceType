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
        using var session = new DictationSession("auto", new NoOpAsrChannel());
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

    private sealed class NoOpAsrChannel : IAsrChannel
    {
        public string CurrentText => "";
        public void Accept(float[] samples) { }
        public void Finish() { }
        public bool IsReady() => false;
        public string Decode() => "";
        public void Dispose() { }
    }
}
