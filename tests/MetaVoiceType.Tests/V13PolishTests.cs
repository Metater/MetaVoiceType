using System.Globalization;
using MetaVoiceType.Audio;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Sessions;
using MetaVoiceType.Storage;
using MetaVoiceType.VoiceCommands;
using MetaVoiceType.Diagnostics;
using MetaVoiceType.Transcription;
using Microsoft.Extensions.Logging.Abstractions;

namespace MetaVoiceType.Tests;

public sealed class V13PolishTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MetaVoiceType.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SchemaFourMigrationPreservesAliasesAndGroupsLegacyReplacementDestinations()
    {
        var legacy = new AppSettings
        {
            SchemaVersion = 3,
            CommandOverrides = new() { ["en-us"] = new() { ["pasteHere"] = "put it here" } },
            CustomCommands = [new() { Phrase = "open notes", Name = "Notes" }],
            WordReplacements =
            [
                new() { Id = "a", Match = "meta voice", Replacement = "MetaVoiceType" },
                new() { Id = "b", Match = "meta voice type", Replacement = "MetaVoiceType" },
                new() { Id = "c", Match = "c sharp", Replacement = "C#" }
            ]
        };

        AppSettings migrated = JsonSettingsStore.Migrate(legacy);

        Assert.Equal(6, migrated.SchemaVersion);
        Assert.Equal(["put it here"], migrated.CommandAliases["en-us"]["pasteRecording"]);
        Assert.Equal(["open notes"], Assert.Single(migrated.CustomCommands).Aliases);
        Assert.Equal(2, migrated.WordReplacementGroups.Count);
        Assert.Equal(["meta voice", "meta voice type"], migrated.WordReplacementGroups.Single(x => x.Replacement == "MetaVoiceType").Matches);
    }

    [Fact]
    public void EnglishPasteRecordingKeepsPasteHereAsSecondaryDefaultAlias()
    {
        VoiceCommandLanguage english = VoiceCommandCatalog.LoadBundled().Get("en-us");
        Assert.Equal("paste recording", english.Commands["pasteRecording"]);
        Assert.Contains("paste here", english.CommandAliases!["pasteRecording"]);
    }

    [Fact]
    public async Task HistoryStoresUtcAndRejectsWhitespaceOnlyRecords()
    {
        var store = new JsonHistoryStore(new AppPaths(_root), NullLogger<JsonHistoryStore>.Instance);
        DateTimeOffset offset = new(2026, 7, 10, 12, 30, 0, TimeSpan.FromHours(5.5));
        await store.AddAsync(new("blank", offset, offset, DictationStatus.Completed, "auto", "  ", false, false, false), TestContext.Current.CancellationToken);
        await store.AddAsync(new("kept", offset, offset.AddMinutes(1), DictationStatus.Completed, "auto", " text ", false, false, false), TestContext.Current.CancellationToken);

        TranscriptRecord record = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.Zero, record.StartedAt.Offset);
        Assert.Equal(TimeSpan.Zero, record.StoppedAt.Offset);
        Assert.Equal("text", record.Text);
        await store.DeleteAllAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void LocalTimestampUsesDstAwareAsciiOffset()
    {
        TimeZoneInfo zone = CreateEasternTestZone();
        string summer = TranscriptTimeFormatter.Format(new DateTimeOffset(2026, 7, 1, 16, 0, 0, TimeSpan.Zero), zone, CultureInfo.InvariantCulture);
        string winter = TranscriptTimeFormatter.Format(new DateTimeOffset(2026, 1, 1, 17, 0, 0, TimeSpan.Zero), zone, CultureInfo.InvariantCulture);
        Assert.Contains("(UTC-04:00)", summer);
        Assert.Contains("(UTC-05:00)", winter);
        Assert.DoesNotContain('−', summer);
    }

    [Theory]
    [InlineData("Ctrl+Alt+ScrollLock", "Ctrl+Alt+ScrollLock")]
    [InlineData("Ctrl+Shift+Pause", "Ctrl+Shift+Pause")]
    public void UncommonShortcutKeysRoundTrip(string input, string expected) => Assert.Equal(expected, ShortcutGestureParser.Parse(input).ToString());

    [Fact]
    public void EveryBuiltInActionHasADistinctCueSignature()
    {
        var signatures = Enum.GetValues<VoiceCommand>().Select(AudioCueService.Describe).ToArray();
        Assert.Equal(signatures.Length, signatures.Distinct().Count());
        Assert.Equal(0, AudioCueService.GainForVolume(0));
        Assert.Equal(0.08, AudioCueService.GainForVolume(0.5), 3);
        Assert.Equal(0.16, AudioCueService.GainForVolume(2), 3);
    }

    [Fact]
    public void CpuOnlyIsOptInRuntimeConfigurationNotModelMetadata()
    {
        var options = new StartupOptions(false, false, false, false, false, false, null, "auto");
        var runtime = new SherpaRuntimeBootstrapper(new AppPaths(_root), options, NullLogger<SherpaRuntimeBootstrapper>.Instance);
        Assert.False(runtime.ForceCpu);
        runtime.SetUserForceCpu(true);
        Assert.True(runtime.ForceCpu);
        string catalog = File.ReadAllText(Path.Combine(FindRoot(), "src", "MetaVoiceType", "Resources", "model-catalog.json"));
        Assert.DoesNotContain("\"acceleration\"", catalog, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"estimatedDownloadBytes\"", catalog);
    }

    [Fact]
    public void VadTailClosureBudgetIsReducedFromV12()
    {
        const double v12BudgetMilliseconds = 480; // ceil(0.45 s / 32 ms) * 32 ms
        Assert.Equal(320, SherpaVadSegmenter.TailClosureBudgetMilliseconds);
        Assert.True(SherpaVadSegmenter.TailClosureBudgetMilliseconds < v12BudgetMilliseconds);
        Assert.Equal(512, SherpaVadSegmenter.WindowSize);
    }

    [Fact]
    public async Task SpectrumPublishesOneSharedLogBandFrame()
    {
        var audio = new FakeAudio();
        await using var spectrum = new AudioSpectrumService(audio);
        using IDisposable lease = spectrum.Acquire();
        var ready = new TaskCompletionSource<IReadOnlyList<double>>(TaskCreationOptions.RunContinuationsAsynchronously);
        spectrum.FrameReady += (_, frame) => ready.TrySetResult(frame);
        float[] samples = Enumerable.Range(0, 4096).Select(index => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * index / AudioFrame.SampleRate))).ToArray();
        audio.Emit(new(samples, Pcm16Converter.ToPcm16(samples), DateTimeOffset.UtcNow));
        IReadOnlyList<double> frame = await ready.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(AudioSpectrumService.BarCount, frame.Count);
        Assert.Contains(frame, value => value > 0.25);
        Assert.Same(frame, spectrum.CurrentFrame);
    }

    [Fact]
    public void PillDeclaresTransparentCornerSurfaceAndSharedSpectrum()
    {
        string pill = File.ReadAllText(Path.Combine(FindRoot(), "src", "MetaVoiceType", "UI", "Views", "PillWindow.axaml"));
        Assert.Contains("Background=\"#00000000\"", pill);
        Assert.Contains("TransparencyLevelHint=\"Transparent\"", pill);
        Assert.Contains("SpectrumControl", pill);
        Assert.Contains("ActivitySpinner", pill);
    }

    private static TimeZoneInfo CreateEasternTestZone()
    {
        TimeZoneInfo.TransitionTime start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        TimeZoneInfo.TransitionTime end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        TimeZoneInfo.AdjustmentRule rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Test Eastern", TimeSpan.FromHours(-5), "Test Eastern", "EST", "EDT", [rule]);
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public event EventHandler<AudioFrame>? FrameReady;
        public event EventHandler<double>? LevelChanged;
        public bool IsRunning => true;
        public AudioMetrics Metrics => new(0, 0, 0, 0, 0);
        public IReadOnlyList<AudioDevice> EnumerateDevices() => [];
        public Task StartAsync(string? deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Emit(AudioFrame frame) { FrameReady?.Invoke(this, frame); LevelChanged?.Invoke(this, frame.Peak); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
