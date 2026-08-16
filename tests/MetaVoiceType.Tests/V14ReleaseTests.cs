using MetaVoiceType.Audio;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Sessions;
using MetaVoiceType.Storage;

namespace MetaVoiceType.Tests;

public sealed class V14ReleaseTests
{
    [Fact]
    public async Task RecordingShortcutCanRemainHeldUntilRecordingEnds()
    {
        var input = new HeldInput();
        var player = new RecordingEventShortcutPlayer(input);

        await player.RecordingStartedAsync("segment", null, "Ctrl+M", TestContext.Current.CancellationToken);
        Assert.Equal(["down Ctrl+M"], input.Events);

        await player.RecordingEndedAsync("segment", null, TestContext.Current.CancellationToken);
        Assert.Equal(["down Ctrl+M", "up Ctrl+M"], input.Events);

        await player.RecordingEndedAsync("segment", null, TestContext.Current.CancellationToken);
        Assert.Equal(2, input.Events.Count);
    }

    [Fact]
    public async Task ShutdownReleasesEveryHeldRecordingShortcut()
    {
        var input = new HeldInput();
        var player = new RecordingEventShortcutPlayer(input);
        await player.RecordingStartedAsync("one", null, "Alt+F8", TestContext.Current.CancellationToken);
        await player.RecordingStartedAsync("two", null, "Enter", TestContext.Current.CancellationToken);

        await player.ReleaseAllAsync();

        Assert.Contains("up Alt+F8", input.Events);
        Assert.Contains("up Enter", input.Events);
    }

    [Fact]
    public void CaptureBufferReportsQueuedSampleBoundaryForTailDrain()
    {
        var buffer = new AudioFrameBuffer(2);
        Assert.True(buffer.TryEnqueue(new byte[640]));
        Assert.True(buffer.TryEnqueue(new byte[320]));

        AudioMetrics metrics = buffer.Snapshot(0);

        Assert.Equal(480, metrics.SamplesQueued);
        Assert.Equal(2, metrics.FramesCaptured);
    }

    [Fact]
    public void SchemaSixPreservesHeldRecordingShortcut()
    {
        AppSettings migrated = JsonSettingsStore.Migrate(new AppSettings
        {
            SchemaVersion = 5,
            RecordingHeldShortcut = "Ctrl+Shift+M"
        });

        Assert.Equal(6, migrated.SchemaVersion);
        Assert.Equal("Ctrl+Shift+M", migrated.RecordingHeldShortcut);
    }

    [Fact]
    public void ReleaseUiAndPackagingDeclareCreditsBrandingAndDeltaUpdates()
    {
        string root = FindRoot();
        string window = File.ReadAllText(Path.Combine(root, "src", "MetaVoiceType", "UI", "Views", "MainWindow.axaml"));
        string packaging = File.ReadAllText(Path.Combine(root, "scripts", "package.ps1"));
        string props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));

        Assert.Contains("Header=\"Credits\"", window);
        Assert.Contains("nvidia-logo.png", window);
        Assert.Contains("See full command list in the settings", window);
        Assert.Contains("Hold while recording", window);
        Assert.Contains("vpk @downloadArguments", packaging);
        Assert.Contains("--delta BestSize", packaging);
        Assert.Contains("Where-Object Name -ne $currentFullPackage", packaging);
        Assert.Contains("<Version>1.4.0</Version>", props);
    }

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class HeldInput : IKeyboardInputSimulator
    {
        public List<string> Events { get; } = [];
        public Task SendShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default)
        {
            Events.Add("tap " + shortcut);
            return Task.CompletedTask;
        }
        public Task PressShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default)
        {
            Events.Add("down " + shortcut);
            return Task.CompletedTask;
        }
        public Task ReleaseShortcutAsync(ShortcutGesture shortcut, CancellationToken cancellationToken = default)
        {
            Events.Add("up " + shortcut);
            return Task.CompletedTask;
        }
    }
}
