using Avalonia.Headless.XUnit;
using MetaVoiceType.Audio;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Core.State;
using MetaVoiceType.Diagnostics;
using MetaVoiceType.Sessions;
using MetaVoiceType.Storage;
using MetaVoiceType.Transcription;
using MetaVoiceType.VoiceCommands;
using Microsoft.Extensions.Logging.Abstractions;

namespace MetaVoiceType.Tests;

public sealed class OrchestratorRaceTests
{
    [AvaloniaFact]
    public async Task RecordingEventShortcutsCoverStartStopPasteCancelAndContinueExactlyOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "MetaVoiceType.Tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var audio = new FakeAudio();
        var history = new FakeHistory();
        DateTimeOffset priorTime = DateTimeOffset.UtcNow.AddMinutes(-1);
        history.Records.Add(new("prior", priorTime, priorTime.AddSeconds(1), DictationStatus.Completed, "auto", "Prior text.", false, false, false, "prior"));
        var settings = new FakeSettings(new AppSettings
        {
            OnboardingComplete = true,
            CopyOnStop = false,
            RecordingStartedShortcut = "Ctrl+Shift+M",
            RecordingStoppedShortcut = "Ctrl+Shift+M"
        });
        var input = new FakeInput();
        using var paste = new PasteCoordinator(new FakeClipboard(), new FakeInsertion(), NullLogger<PasteCoordinator>.Instance);
        var orchestrator = new ApplicationOrchestrator(audio, history, settings, paste,
            new DecodeCoordinator(NullLogger<DecodeCoordinator>.Instance), new RecoveryWriter(paths, NullLogger<RecoveryWriter>.Instance),
            new VoskCommandRecognizer(NullLogger<VoskCommandRecognizer>.Instance),
            new CustomCommandExecutor(input, NullLogger<CustomCommandExecutor>.Instance), new RecordingEventShortcutPlayer(input),
            new FakeCues(), new MetaVoiceTypeState(),
            new SherpaRuntimeBootstrapper(paths, new(false, false, false, true, false, false, null, "auto"), NullLogger<SherpaRuntimeBootstrapper>.Instance),
            NullLoggerFactory.Instance, NullLogger<ApplicationOrchestrator>.Instance)
        { SegmenterFactory = _ => new FlushSegmenter([]) };
        try
        {
            await orchestrator.InitializeAsync(TestContext.Current.CancellationToken);
            orchestrator.SetBackendForTesting(new BlockingBackend(""));

            Assert.True(orchestrator.StartRecording()); Assert.True(orchestrator.StopRecording());
            Assert.True(orchestrator.StartRecording()); Assert.Equal(PasteRequestResult.Accepted, orchestrator.PasteHere());
            Assert.True(orchestrator.StartRecording()); Assert.True(orchestrator.StopRecording(canceled: true));
            Assert.True(orchestrator.ContinueRecording()); Assert.True(orchestrator.StopRecording());

            Assert.Equal(8, input.Values.Count);
            Assert.All(input.Values, value => Assert.Equal("Ctrl+Shift+M", value));
        }
        finally { await orchestrator.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task StopThenPasteWhileFinalizingPastesThatExactSessionOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "MetaVoiceType.Tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var audio = new FakeAudio();
        var history = new FakeHistory();
        var settings = new FakeSettings(new AppSettings { OnboardingComplete = true, CopyOnStop = false });
        var clipboard = new FakeClipboard();
        var insertion = new FakeInsertion();
        using var paste = new PasteCoordinator(clipboard, insertion, NullLogger<PasteCoordinator>.Instance);
        var decode = new DecodeCoordinator(NullLogger<DecodeCoordinator>.Instance);
        var recovery = new RecoveryWriter(paths, NullLogger<RecoveryWriter>.Instance);
        var commands = new VoskCommandRecognizer(NullLogger<VoskCommandRecognizer>.Instance);
        var shortcutPlayer = new RecordingEventShortcutPlayer(new FakeInput());
        var backend = new BlockingBackend("race transcript");
        var runtime = new SherpaRuntimeBootstrapper(paths, new(false, false, false, true, false, false, null, "auto"), NullLogger<SherpaRuntimeBootstrapper>.Instance);
        var orchestrator = new ApplicationOrchestrator(audio, history, settings, paste, decode, recovery, commands,
            new CustomCommandExecutor(new FakeInput(), NullLogger<CustomCommandExecutor>.Instance), shortcutPlayer,
            new FakeCues(), new MetaVoiceTypeState(), runtime, NullLoggerFactory.Instance, NullLogger<ApplicationOrchestrator>.Instance)
        {
            SegmenterFactory = _ => new FlushSegmenter(new float[4_000])
        };
        CancellationToken token = TestContext.Current.CancellationToken;
        try
        {
            await orchestrator.InitializeAsync(token);
            orchestrator.SetBackendForTesting(backend);
            Assert.True(orchestrator.StartRecording());
            audio.Emit(Pcm16Converter.Convert(new byte[8_000]));
            Assert.True(orchestrator.StopRecording());
            await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

            Assert.Equal(PasteRequestResult.Accepted, orchestrator.PasteHere());
            backend.Release.TrySetResult();
            await insertion.Pasted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (history.Records.Count == 0) await Task.Delay(10, timeout.Token);

            Assert.Equal(["race transcript"], clipboard.Values);
            Assert.Equal(1, insertion.Count);
            Assert.Single(history.Records);
            Assert.Equal("race transcript", history.Records[0].Text);
        }
        finally
        {
            await orchestrator.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [AvaloniaFact]
    public async Task OldPasteCanRemainActiveWhileANewRecordingStartsAndCompletesUnaffected()
    {
        string root = Path.Combine(Path.GetTempPath(), "MetaVoiceType.Tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var audio = new FakeAudio();
        var history = new FakeHistory();
        var insertion = new BlockingPasteInsertion();
        var state = new MetaVoiceTypeState();
        using var paste = new PasteCoordinator(new FakeClipboard(), insertion, NullLogger<PasteCoordinator>.Instance);
        var backend = new BlockingBackend("recording A");
        var orchestrator = new ApplicationOrchestrator(audio, history, new FakeSettings(new AppSettings { OnboardingComplete = true, CopyOnStop = false }), paste,
            new DecodeCoordinator(NullLogger<DecodeCoordinator>.Instance), new RecoveryWriter(paths, NullLogger<RecoveryWriter>.Instance),
            new VoskCommandRecognizer(NullLogger<VoskCommandRecognizer>.Instance),
            new CustomCommandExecutor(new FakeInput(), NullLogger<CustomCommandExecutor>.Instance), new RecordingEventShortcutPlayer(new FakeInput()),
            new FakeCues(), state,
            new SherpaRuntimeBootstrapper(paths, new(false, false, false, true, false, false, null, "auto"), NullLogger<SherpaRuntimeBootstrapper>.Instance),
            NullLoggerFactory.Instance, NullLogger<ApplicationOrchestrator>.Instance)
        { SegmenterFactory = _ => new FlushSegmenter(new float[4_000]) };
        CancellationToken token = TestContext.Current.CancellationToken;
        try
        {
            await orchestrator.InitializeAsync(token);
            orchestrator.SetBackendForTesting(backend);
            Assert.True(orchestrator.StartRecording());
            audio.Emit(Pcm16Converter.Convert(new byte[8_000]));
            Assert.Equal(PasteRequestResult.Accepted, orchestrator.PasteHere());
            await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            backend.Release.TrySetResult();
            await insertion.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

            Assert.True(paste.IsActive);
            Assert.True(orchestrator.StartRecording());
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.True(state.IsRecording);
            insertion.Release.TrySetResult();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (paste.IsActive) await Task.Delay(10, timeout.Token);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.True(state.IsRecording);
            Assert.True(orchestrator.StopRecording());
        }
        finally
        {
            insertion.Release.TrySetResult();
            await orchestrator.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class BlockingBackend(string text) : IAsrBackend
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AsrRuntimeStatus Status { get; } = new("test", "Test", "cpu", "CPU", null, "test", null);
        public string Transcribe(float[] samples) { Started.TrySetResult(); Release.Task.GetAwaiter().GetResult(); return text; }
        public void Dispose() { }
    }

    private sealed class FlushSegmenter(float[] samples) : ISpeechSegmenter
    {
        public IReadOnlyList<SpeechAudioSegment> Accept(ReadOnlySpan<float> input) => [];
        public IReadOnlyList<SpeechAudioSegment> Flush() => [new(0, samples)];
        public void Dispose() { }
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public event EventHandler<AudioFrame>? FrameReady;
        public event EventHandler<double>? LevelChanged;
        public bool IsRunning { get; private set; }
        public AudioMetrics Metrics => new(0, 0, 0, 0, 0);
        public IReadOnlyList<AudioDevice> EnumerateDevices() => [new("test", "Test microphone", true)];
        public Task StartAsync(string? deviceId, CancellationToken cancellationToken = default) { IsRunning = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { IsRunning = false; return Task.CompletedTask; }
        public void Emit(AudioFrame frame) { FrameReady?.Invoke(this, frame); LevelChanged?.Invoke(this, frame.Peak); }
        public ValueTask DisposeAsync() { IsRunning = false; return ValueTask.CompletedTask; }
    }

    private sealed class FakeHistory : IHistoryStore
    {
        public List<TranscriptRecord> Records { get; } = [];
        public Task<IReadOnlyList<TranscriptRecord>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TranscriptRecord>>(Records.ToArray());
        public Task AddAsync(TranscriptRecord record, CancellationToken cancellationToken = default) { Records.RemoveAll(x => x.LogicalId == record.LogicalId); Records.Add(record); return Task.CompletedTask; }
        public Task DeleteAsync(string logicalTranscriptId, CancellationToken cancellationToken = default) { Records.RemoveAll(x => x.LogicalId == logicalTranscriptId); return Task.CompletedTask; }
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) { Records.Clear(); return Task.CompletedTask; }
    }

    private sealed class FakeSettings(AppSettings value) : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(value);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> Values { get; } = [];
        public Task SetTextAsync(string text, CancellationToken cancellationToken = default) { Values.Add(text); return Task.CompletedTask; }
    }

    private sealed class FakeInsertion : ITextInsertionService
    {
        public TaskCompletionSource Pasted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count { get; private set; }
        public Task PasteAsync(CancellationToken cancellationToken = default) { Count++; Pasted.TrySetResult(); return Task.CompletedTask; }
    }

    private sealed class BlockingPasteInsertion : ITextInsertionService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PasteAsync(CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FakeInput : IKeyboardInputSimulator
    {
        public List<string> Values { get; } = [];
        public Task SendShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default) { Values.Add(shortcut.ToString()); return Task.CompletedTask; }
    }

    private sealed class FakeCues : IAudioCueService
    {
        public void PlayAccepted(VoiceCommand command, double volume) { }
        public void PlayError(double volume) { }
        public void PlayRecovered(double volume) { }
    }

}
