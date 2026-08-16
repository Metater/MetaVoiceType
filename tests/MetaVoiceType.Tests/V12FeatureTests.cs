using MetaVoiceType.Audio;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Sessions;
using MetaVoiceType.Storage;
using MetaVoiceType.Transcription;
using MetaVoiceType.VoiceCommands;
using Microsoft.Extensions.Logging.Abstractions;
using SharpHook.Data;

namespace MetaVoiceType.Tests;

public sealed class V12FeatureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MetaVoiceType.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("the cat is here", "the dog is here")]
    [InlineData("CAT, cat!", "dog, dog!")]
    [InlineData("cat at the beginning", "dog at the beginning")]
    [InlineData("at the end cat", "at the end dog")]
    [InlineData("concatenate bobcat", "concatenate bobcat")]
    public void WordReplacementIsLiteralCaseInsensitiveAndBoundaryAware(string input, string expected)
    {
        var rule = new WordReplacement { Match = "cat", Replacement = "dog" };
        Assert.Equal(expected, WordReplacementEngine.Apply(input, [rule]));
    }

    [Fact]
    public void WordReplacementUsesLongestPhraseThenStableOrderAndPreservesReplacementCase()
    {
        WordReplacement[] rules =
        [
            new() { Id = "b", Match = "meta", Replacement = "wrong" },
            new() { Id = "a", Match = "meta voice type", Replacement = "MetaVoiceType" },
            new() { Id = "c", Match = "c sharp", Replacement = "C#" },
            new() { Id = "d", Match = "привет", Replacement = "Здравствуйте" }
        ];
        Assert.Equal("MetaVoiceType is written in C#. Здравствуйте!", WordReplacementEngine.Apply("meta voice type is written in c sharp. ПРИВЕТ!", rules));
        Assert.Throws<InvalidDataException>(() => WordReplacementEngine.Validate(new WordReplacement { Match = " " }));
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("Ctrl+S")]
    [InlineData("Alt+F4")]
    [InlineData("F5")]
    [InlineData("Ctrl+Shift+K")]
    public void ActionShortcutsSupportSingleKeysAndHaveExactDownUpOrder(string text)
    {
        ShortcutGesture gesture = ShortcutGestureParser.ParseAction(text);
        IReadOnlyList<KeyboardStroke> sequence = gesture.PlaybackSequence();
        Assert.Equal(gesture.Modifiers.Select(x => new KeyboardStroke(x, true)), sequence.Take(gesture.Modifiers.Count));
        Assert.Equal(new KeyboardStroke(gesture.Key, true), sequence[gesture.Modifiers.Count]);
        Assert.Equal(new KeyboardStroke(gesture.Key, false), sequence[gesture.Modifiers.Count + 1]);
        Assert.Equal(gesture.Modifiers.Reverse().Select(x => new KeyboardStroke(x, false)), sequence.Skip(gesture.Modifiers.Count + 2));
    }

    [Fact]
    public async Task RecordingEventShortcutsFireExactlyOncePerLifecycle()
    {
        var input = new FakeInput();
        var player = new RecordingEventShortcutPlayer(input);
        await player.RecordingStartedAsync("segment", "Ctrl+Shift+M", TestContext.Current.CancellationToken);
        await player.RecordingStartedAsync("segment", "Ctrl+Shift+M", TestContext.Current.CancellationToken);
        await player.RecordingEndedAsync("segment", "Ctrl+Shift+M", TestContext.Current.CancellationToken);
        await player.RecordingEndedAsync("segment", "Ctrl+Shift+M", TestContext.Current.CancellationToken);
        Assert.Equal(["Ctrl+Shift+M", "Ctrl+Shift+M"], input.Values);
    }

    [Fact]
    public void PreRollSlicesAtCommandBoundaryWithoutGapOrDuplication()
    {
        float[] source = Enumerable.Range(0, 1600).Select(x => x / 2000f).ToArray();
        var buffer = new AudioPreRollBuffer(1600);
        buffer.Add(0, Frame(source[..800]));
        buffer.Add(800, Frame(source[800..]));
        float[] replay = buffer.Snapshot(600, 1200).SelectMany(x => x.Samples).ToArray();
        Assert.Equal(source[600..1200], replay);
        Assert.Equal(600, replay.Length);
    }

    [Fact]
    public async Task SyntheticSustainedCaptureQueueDropsNoFramesAndFullyDrains()
    {
        const int count = 20_000;
        var queue = new AudioFrameBuffer(count);
        for (int i = 0; i < count; i++) Assert.True(queue.TryEnqueue(new byte[640]));
        queue.Complete();
        int delivered = 0;
        await foreach (byte[] frame in queue.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(640, frame.Length);
            delivered++;
        }
        AudioMetrics metrics = queue.Snapshot(0);
        Assert.Equal(count, delivered);
        Assert.Equal(count, metrics.FramesCaptured);
        Assert.Equal(count, metrics.FramesDispatched);
        Assert.Equal(0, metrics.FramesDropped);
        Assert.Equal(0, metrics.QueueDepth);
        Assert.Equal(count, metrics.CaptureQueueHighWaterMark);
    }

    [Fact]
    public async Task ContinuedTranscriptRemainsOneLogicalHistoryEntry()
    {
        DateTimeOffset originalStart = DateTimeOffset.UtcNow.AddMinutes(-1);
        var prior = new TranscriptRecord("logical", originalStart, originalStart.AddSeconds(2), DictationStatus.Completed, "auto", "Hello world.", false, true, false,
            "logical", originalStart.AddSeconds(2), 1, 2);
        using var session = new DictationSession("auto", 0, new FakeBackend("This is more text."), new FlushSegmenter(new float[1600]), continuedRecord: prior);
        var decode = new DecodeCoordinator(NullLogger<DecodeCoordinator>.Instance);
        decode.Start();
        decode.Finalize(session, session.Stop(false, false));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (session.Status == DictationStatus.Finalizing) await Task.Delay(10, timeout.Token);
        Assert.Equal("Hello world. This is more text.", session.FinalText);
        Assert.Equal("logical", session.LogicalTranscriptId);
        Assert.Equal(originalStart, session.LogicalStartedAt);
        await decode.DisposeAsync();
    }

    [Fact]
    public async Task HistoryUpsertsAndDeletesByLogicalTranscript()
    {
        var store = new JsonHistoryStore(new AppPaths(_root), NullLogger<JsonHistoryStore>.Instance);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CancellationToken token = TestContext.Current.CancellationToken;
        await store.AddAsync(new("logical", now, now, DictationStatus.Completed, "auto", "A", false, false, false, "logical"), token);
        await store.AddAsync(new("logical", now, now.AddSeconds(1), DictationStatus.Completed, "auto", "A B", false, false, false, "logical", now, 2), token);
        Assert.Equal("A B", Assert.Single(await store.LoadAsync(token)).Text);
        await store.DeleteAsync("logical", token);
        Assert.Empty(await store.LoadAsync(token));
    }

    [Fact]
    public void SettingsMigrationPreservesV11ChoicesAndFreshThemeIsSystem()
    {
        Assert.Equal(AppTheme.System, new AppSettings().Theme);
        AppSettings migrated = JsonSettingsStore.Migrate(new AppSettings { SchemaVersion = 2, Theme = AppTheme.Light, ToggleHotkey = "Ctrl+K" });
        Assert.Equal(6, migrated.SchemaVersion);
        Assert.Equal(AppTheme.Light, migrated.Theme);
        Assert.Equal("Ctrl+K", migrated.ToggleHotkey);
    }

    [Fact]
    public void MainCommandCopyUsesActiveLanguageConfigurationAndRecordingState()
    {
        var english = new Dictionary<VoiceCommand, string> { [VoiceCommand.StartRecording] = "begin", [VoiceCommand.StopRecording] = "finish" };
        var french = new Dictionary<VoiceCommand, string> { [VoiceCommand.StartRecording] = "commencer", [VoiceCommand.StopRecording] = "terminer" };
        Assert.Equal("Say \"begin\"", VoiceCommandCopy.ForRecordingState(false, english));
        Assert.Equal("Say \"finish\"", VoiceCommandCopy.ForRecordingState(true, english));
        Assert.Equal("Say \"commencer\"", VoiceCommandCopy.ForRecordingState(false, french));
    }

    [Fact]
    public void UiContainsRequiredWarningAndTransparentStablePill()
    {
        string root = FindRoot();
        string main = File.ReadAllText(Path.Combine(root, "src", "MetaVoiceType", "UI", "Views", "MainWindow.axaml"));
        string pill = File.ReadAllText(Path.Combine(root, "src", "MetaVoiceType", "UI", "Views", "PillWindow.axaml"));
        Assert.Contains("Short or common phrases may trigger accidentally", main);
        Assert.Contains("TransparencyLevelHint=\"Transparent\"", pill);
        Assert.DoesNotContain("PointerEntered", pill);
        Assert.DoesNotContain("ProgressBar", pill);
    }

    private static AudioFrame Frame(float[] samples) => new(samples, Pcm16Converter.ToPcm16(samples), DateTimeOffset.UtcNow);
    private sealed class FakeInput : IKeyboardInputSimulator
    {
        public List<string> Values { get; } = [];
        public Task SendShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default) { Values.Add(shortcut.ToString()); return Task.CompletedTask; }
    }
    private sealed class FakeBackend(string text) : IAsrBackend
    {
        public AsrRuntimeStatus Status { get; } = new("test", "Test", "cpu", "CPU", null, "test", null);
        public string Transcribe(float[] samples) => text;
        public void Dispose() { }
    }
    private sealed class FlushSegmenter(float[] samples) : ISpeechSegmenter
    {
        public IReadOnlyList<SpeechAudioSegment> Accept(ReadOnlySpan<float> input) => [];
        public IReadOnlyList<SpeechAudioSegment> Flush() => [new(0, samples)];
        public void Dispose() { }
    }
    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
