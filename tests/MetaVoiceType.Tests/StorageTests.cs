using MetaVoiceType.Core.Models;
using MetaVoiceType.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MetaVoiceType.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MetaVoiceType.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SettingsRoundTripPreservesValues()
    {
        var paths = new AppPaths(_root);
        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);
        var expected = new AppSettings { Theme = AppTheme.Light, VoiceCommandLanguage = "uk", CueVolume = 0.25 };

        await store.SaveAsync(expected, TestContext.Current.CancellationToken);
        AppSettings actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected.Theme, actual.Theme);
        Assert.Equal("uk", actual.VoiceCommandLanguage);
        Assert.Equal(0.25, actual.CueVolume);
    }

    [Fact]
    public async Task CorruptSettingsFallBackToDefaults()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(paths.SettingsFile, "{ absolutely not json", TestContext.Current.CancellationToken);
        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);

        AppSettings actual = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new AppSettings().VoiceCommandLanguage, actual.VoiceCommandLanguage);
    }

    [Fact]
    public async Task V11JsonMigratesWithoutLosingThemeHotkeyCommandsOrCustomActions()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(_root);
        const string json = """
            {
              "schemaVersion": 1,
              "onboardingComplete": true,
              "theme": 0,
              "toggleHotkey": "Ctrl+Alt+K",
              "muteDiscordWhileRecording": true,
              "commandOverrides": { "en-us": { "pasteHere": "put it there" } },
              "customCommands": [ { "id": "one", "name": "Enter", "voiceCommandLanguageId": "en-us", "phrase": "enter", "commandType": 3, "shortcut": "Enter" } ]
            }
            """;
        await File.WriteAllTextAsync(paths.SettingsFile, json, TestContext.Current.CancellationToken);
        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);
        AppSettings loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, loaded.SchemaVersion);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal("Ctrl+Alt+K", loaded.ToggleHotkey);
        Assert.Equal("put it there", loaded.CommandOverrides["en-us"]["pasteHere"]);
        Assert.Equal("Enter", Assert.Single(loaded.CustomCommands).Shortcut);
        Assert.Empty(loaded.WordReplacements);
    }

    [Fact]
    public async Task AtomicWriteLeavesNoTemporaryFiles()
    {
        var paths = new AppPaths(_root);
        var store = new JsonSettingsStore(paths, NullLogger<JsonSettingsStore>.Instance);
        await store.SaveAsync(new AppSettings(), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(paths.SettingsFile));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp-*"));
    }

    [Fact]
    public async Task HistoryKeepsNewestHundred()
    {
        var paths = new AppPaths(_root);
        var store = new JsonHistoryStore(paths, NullLogger<JsonHistoryStore>.Instance);
        for (int i = 0; i < 101; i++)
        {
            var now = DateTimeOffset.UtcNow.AddSeconds(i);
            await store.AddAsync(new TranscriptRecord(i.ToString(System.Globalization.CultureInfo.InvariantCulture), now, now, DictationStatus.Completed, "auto", $"text-{i}", false, false, false), TestContext.Current.CancellationToken);
        }

        IReadOnlyList<TranscriptRecord> records = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(100, records.Count);
        Assert.Equal("100", records[0].SessionId);
        Assert.DoesNotContain(records, x => x.SessionId == "0");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
