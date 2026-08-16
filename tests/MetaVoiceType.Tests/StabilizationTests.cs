using System.Text.Json;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Core.State;
using MetaVoiceType.Storage;
using MetaVoiceType.VoiceCommands;
using MetaVoiceType.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace MetaVoiceType.Tests;

public sealed class StabilizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MetaVoiceType.StabilizationTests", Guid.NewGuid().ToString("N"));

    public static TheoryData<string, string[]> MigrationFixtures => new()
    {
        { "{\"schemaVersion\":5}", [] },
        { "{\"schemaVersion\":1,\"onboardingComplete\":true,\"commandOverrides\":{\"en-us\":{\"stopRecording\":\"finish now\"}}}", [] },
        { "{\"schemaVersion\":2,\"commandOverrides\":{\"en-us\":{\"pasteHere\":\"paste here\"}}}", ["paste recording", "paste here"] },
        { "{\"schemaVersion\":4,\"commandAliases\":{\"en-us\":{\"pasteHere\":[\"paste recording\",\"paste here\"]}}}", ["paste recording", "paste here"] },
        { "{\"schemaVersion\":2,\"commandOverrides\":{\"en-us\":{\"pasteHere\":\"insert that\"}}}", ["insert that"] },
        { "{\"schemaVersion\":5,\"commandAliases\":{\"en-us\":{\"pasteRecording\":[\"paste recording\",\"paste here\"]}}}", ["paste recording", "paste here"] }
    };

    [Theory]
    [MemberData(nameof(MigrationFixtures))]
    public void CommandMigrationFixturesAreDeterministicAndIdempotent(string json, string[] expectedPasteAliases)
    {
        AppSettings source = JsonSerializer.Deserialize<AppSettings>(json, AtomicJsonFile.Options)!;
        AppSettings once = JsonSettingsStore.Migrate(source);
        AppSettings twice = JsonSettingsStore.Migrate(once);

        Assert.Equal(6, once.SchemaVersion);
        Assert.Equal(JsonSerializer.Serialize(once, AtomicJsonFile.Options), JsonSerializer.Serialize(twice, AtomicJsonFile.Options));
        Assert.All(once.CommandAliases.Values, language => Assert.All(language.Values, aliases =>
            Assert.Equal(aliases.Count, aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count())));
        Assert.DoesNotContain(once.CommandAliases.Values, language => language.ContainsKey(VoiceCommandKeys.LegacyPasteHere));
        if (expectedPasteAliases.Length > 0) Assert.Equal(expectedPasteAliases, once.CommandAliases["en-us"]["pasteRecording"]);
        _ = VoiceCommandSchema.BuildDefinitions(once, VoiceCommandCatalog.LoadBundled().Get("en-us"));
    }

    [Fact]
    public async Task MigratedSettingsFileDoesNotChangeAgainAfterRestart()
    {
        Directory.CreateDirectory(_root);
        string settingsFile = new AppPaths(_root).SettingsFile;
        await File.WriteAllTextAsync(settingsFile,
            "{\"schemaVersion\":1,\"onboardingComplete\":true,\"commandOverrides\":{\"en-us\":{\"pasteHere\":\"insert that\"}}}",
            TestContext.Current.CancellationToken);
        using var store = new JsonSettingsStore(new AppPaths(_root), NullLogger<JsonSettingsStore>.Instance);

        AppSettings first = await store.LoadAsync(TestContext.Current.CancellationToken);
        string firstFile = await File.ReadAllTextAsync(settingsFile, TestContext.Current.CancellationToken);
        AppSettings second = await store.LoadAsync(TestContext.Current.CancellationToken);
        string secondFile = await File.ReadAllTextAsync(settingsFile, TestContext.Current.CancellationToken);

        Assert.True(first.SetupCompletedOnce);
        Assert.Equal(["insert that"], second.CommandAliases["en-us"]["pasteRecording"]);
        Assert.Equal(firstFile, secondFile);
    }

    [Fact]
    public void EnglishPasteRecordingIsOneActionWithTwoAliasesAndAValidRestrictedGrammar()
    {
        VoiceCommandLanguage english = VoiceCommandCatalog.LoadBundled().Get("en-us");
        IReadOnlyList<VoiceCommandDefinition> definitions = VoiceCommandSchema.BuildDefinitions(new AppSettings(), english);

        VoiceCommandDefinition paste = Assert.Single(definitions, x => x.BuiltInCommand == VoiceCommand.PasteRecording);
        Assert.Equal("pasteRecording", paste.Id);
        Assert.Equal(["paste recording", "paste here"], paste.Aliases);
        using JsonDocument grammar = JsonDocument.Parse(VoskCommandRecognizer.BuildGrammar(definitions));
        string[] phrases = grammar.RootElement.EnumerateArray().Select(x => x.GetString()!).ToArray();
        Assert.Contains("paste recording", phrases);
        Assert.Contains("paste here", phrases);
        Assert.Contains("[unk]", phrases);
        Assert.True(VoskCommandRecognizer.SupportsManagedGrammar(definitions));
    }

    [Fact]
    public void CommandSchemaDeduplicatesSameActionAliasesAndRejectsAmbiguityOrMalformedActions()
    {
        IReadOnlyList<VoiceCommandDefinition> normalized = CommandPhraseValidator.NormalizeDefinitions(
            [new("one", ["Same phrase", " same  phrase "])]);
        Assert.Equal(["same phrase"], Assert.Single(normalized).Aliases);
        Assert.Throws<InvalidDataException>(() => CommandPhraseValidator.NormalizeDefinitions(
            [new("one", ["same phrase"]), new("two", ["same phrase"])]));
        Assert.Throws<InvalidDataException>(() => CommandPhraseValidator.NormalizeDefinitions(
            [new("one", ["first"]), new("one", ["second"])]));
        Assert.Throws<InvalidDataException>(() => CommandPhraseValidator.NormalizeDefinitions([new("one", [])]));
        Assert.Throws<InvalidDataException>(() => CommandPhraseValidator.NormalizeDefinitions([new("one", ["first", " "])]));
        Assert.Throws<InvalidDataException>(() => CommandPhraseValidator.NormalizeDefinitions([new("one", ["[unk]"])]));

        var malformedSettings = new AppSettings
        {
            CustomCommands = [new() { Id = "broken", VoiceCommandLanguageId = "en-us", Aliases = ["valid", " "] }]
        };
        Assert.Throws<InvalidDataException>(() => VoiceCommandSchema.ValidateSettings(malformedSettings, VoiceCommandCatalog.LoadBundled()));
    }

    [Fact]
    public void BuiltInRegistryHasExactlyOneStableActionForEverySupportedCommand()
    {
        VoiceCommandLanguage english = VoiceCommandCatalog.LoadBundled().Get("en-us");
        IReadOnlyList<VoiceCommandDefinition> definitions = VoiceCommandSchema.BuildDefinitions(new AppSettings(), english);

        Assert.Equal(Enum.GetValues<VoiceCommand>().Length, definitions.Count(x => x.BuiltInCommand is not null));
        Assert.Equal(Enum.GetValues<VoiceCommand>().Order(), definitions.Select(x => x.BuiltInCommand!.Value).Order());
        Assert.Equal(VoiceCommandKeys.All.Count, VoiceCommandKeys.All.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(VoiceCommandKeys.LegacyPasteHere, definitions.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
        Assert.All(VoiceCommandKeys.All, pair =>
        {
            VoiceCommandDefinition definition = Assert.Single(definitions, x => x.BuiltInCommand == pair.Key);
            Assert.Equal(pair.Value, definition.Id);
            Assert.NotEmpty(definition.Aliases);
        });
    }

    [Fact]
    public async Task FreshSetupExercisesProductionCommandSchemaAndEnablesInputsOnlyAfterReady()
    {
        AppSettings settings = JsonSettingsStore.Migrate(new AppSettings { SetupCompletedOnce = false });
        VoiceCommandLanguage english = VoiceCommandCatalog.LoadBundled().Get("en-us");
        IReadOnlyList<VoiceCommandDefinition> definitions = VoiceCommandSchema.BuildDefinitions(settings, english);
        using JsonDocument grammar = JsonDocument.Parse(VoskCommandRecognizer.BuildGrammar(definitions));
        Assert.True(grammar.RootElement.GetArrayLength() > definitions.Count);

        var readiness = new ApplicationReadiness();
        var actions = new ApplicationActionCoordinator(readiness);
        var hotkey = new FakeHotkey();
        var registration = new GlobalHotkeyRegistration(hotkey, actions);
        readiness.BeginInitialization(false);
        readiness.SetMicrophoneReady(true);
        readiness.SetDictationReady(true);
        readiness.SetVoiceCommandsReady(true);
        readiness.CompleteInitialization();

        Assert.Equal(ApplicationReadinessState.SetupIncomplete, readiness.State);
        await registration.ApplyAsync("Ctrl+Space", TestContext.Current.CancellationToken);
        Assert.Equal(0, hotkey.StartCount);
        Assert.False(actions.IsAllowed(ApplicationAction.VoiceCommand));
        readiness.MarkSetupCompleted();
        await registration.ApplyAsync("Ctrl+Space", TestContext.Current.CancellationToken);

        Assert.Equal(ApplicationReadinessState.Ready, readiness.State);
        Assert.Equal(1, hotkey.StartCount);
        Assert.True(actions.IsAllowed(ApplicationAction.VoiceCommand));
        Assert.True(actions.IsAllowed(ApplicationAction.CustomAutomation));
        Assert.True(actions.IsAllowed(ApplicationAction.RecordingEventShortcut));
    }

    [Fact]
    public async Task FreshAndInterruptedSetupCannotRegisterGlobalInputsUntilGenuinelyComplete()
    {
        var readiness = new ApplicationReadiness();
        var actions = new ApplicationActionCoordinator(readiness);
        var hotkey = new FakeHotkey();
        var registration = new GlobalHotkeyRegistration(hotkey, actions);

        readiness.BeginInitialization(false);
        readiness.SetMicrophoneReady(true);
        readiness.SetDictationReady(true);
        readiness.CompleteInitialization();
        await registration.ApplyAsync("Ctrl+Space", TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationReadinessState.SetupIncomplete, readiness.State);
        Assert.Equal(0, hotkey.StartCount);
        Assert.False(actions.IsAllowed(ApplicationAction.VoiceCommand));
        Assert.False(actions.IsAllowed(ApplicationAction.CustomAutomation));

        readiness.SetVoiceCommandsReady(true);
        await registration.ApplyAsync("Ctrl+Space", TestContext.Current.CancellationToken);
        Assert.Equal(0, hotkey.StartCount);
        readiness.MarkSetupCompleted();
        await registration.ApplyAsync("Ctrl+Space", TestContext.Current.CancellationToken);
        Assert.Equal(ApplicationReadinessState.Ready, readiness.State);
        Assert.Equal(1, hotkey.StartCount);
    }

    [Fact]
    public async Task PreviouslyCompletedInstallAllowsOnlyManualDictationInDegradedModeAndRepairsToReady()
    {
        var readiness = new ApplicationReadiness();
        var actions = new ApplicationActionCoordinator(readiness);
        var hotkey = new FakeHotkey();
        var registration = new GlobalHotkeyRegistration(hotkey, actions);
        readiness.BeginInitialization(true);
        readiness.SetMicrophoneReady(true);
        readiness.SetDictationReady(true);
        readiness.SetVoiceCommandsReady(false);
        readiness.CompleteInitialization();

        Assert.Equal(ApplicationReadinessState.Degraded, readiness.State);
        Assert.True(actions.IsAllowed(ApplicationAction.ManualRecording));
        Assert.True(actions.IsAllowed(ApplicationAction.GlobalRecordingShortcut));
        Assert.False(actions.IsAllowed(ApplicationAction.VoiceCommand));
        Assert.False(actions.IsAllowed(ApplicationAction.CustomAutomation));
        Assert.False(actions.IsAllowed(ApplicationAction.RecordingEventShortcut));
        await registration.ApplyAsync("Ctrl+Space", TestContext.Current.CancellationToken);
        Assert.Equal(1, hotkey.StartCount);

        readiness.SetVoiceCommandsReady(true);
        Assert.Equal(ApplicationReadinessState.Ready, readiness.State);
        Assert.True(actions.IsAllowed(ApplicationAction.VoiceCommand));
        Assert.True(actions.IsAllowed(ApplicationAction.RecordingEventShortcut));
        await registration.ApplyAsync("Ctrl+Space", TestContext.Current.CancellationToken);
        Assert.Equal(2, hotkey.StartCount);
    }

    [Fact]
    public void OptionalGpuRuntimeNeverBlocksFirstTimeSetup()
    {
        var readiness = new ApplicationReadiness();
        readiness.BeginInitialization(false);
        readiness.CompleteInitialization();
        Assert.False(MainViewModel.ShouldInstallOptionalGpuRuntime(readiness, forceCpuOnly: false, hasNvidiaGpu: true));

        readiness.SetMicrophoneReady(true);
        readiness.SetDictationReady(true);
        readiness.SetVoiceCommandsReady(true);
        readiness.MarkSetupCompleted();
        Assert.True(MainViewModel.ShouldInstallOptionalGpuRuntime(readiness, forceCpuOnly: false, hasNvidiaGpu: true));
        Assert.False(MainViewModel.ShouldInstallOptionalGpuRuntime(readiness, forceCpuOnly: true, hasNvidiaGpu: true));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeHotkey : IGlobalHotkeyService
    {
        public event EventHandler? ToggleRecording { add { } remove { } }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public string ActiveGesture { get; private set; } = "Ctrl+Space";
        public Task StartAsync(string gesture = "Ctrl+Space", CancellationToken cancellationToken = default)
        {
            StartCount++; ActiveGesture = gesture; return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default) { StopCount++; return Task.CompletedTask; }
        public Task<HotkeyChangeResult> ChangeAsync(string gesture, CancellationToken cancellationToken = default)
        {
            ActiveGesture = gesture; return Task.FromResult(new HotkeyChangeResult(true, gesture));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
