using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Core.State;
using MetaVoiceType.Integrations;
using MetaVoiceType.Models;
using MetaVoiceType.Sessions;
using MetaVoiceType.Storage;
using MetaVoiceType.VoiceCommands;

namespace MetaVoiceType.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly string[] ExposedLanguageIds = ["en-us", "ru", "fr", "de", "es", "pt-br", "it", "nl", "uk", "sv", "cs", "pl"];
    private readonly ApplicationOrchestrator _orchestrator;
    private readonly IModelDownloadService _downloads;
    private readonly IStartupService _startup;
    private readonly IGlobalHotkeyService _hotkey;
    private readonly IUpdateService _updates;
    private readonly IAudioCaptureService _audio;
    private readonly IDiscordVoiceIntegration _discord;
    private readonly AppPaths _paths;
    private readonly VoiceCommandCatalog _voiceCatalog = VoiceCommandCatalog.LoadBundled();
    private readonly ModelCatalog _modelCatalog = ModelCatalog.LoadBundled();
    private readonly DispatcherTimer _elapsedTimer;
    private CancellationTokenSource? _downloadCancellation;
    private CancellationTokenSource? _commandSaveDebounce;
    private bool _loading = true;

    public MainViewModel(ApplicationOrchestrator orchestrator, IModelDownloadService downloads,
        IStartupService startup, IGlobalHotkeyService hotkey, IUpdateService updates, IAudioCaptureService audio,
        IDiscordVoiceIntegration discord, AppPaths paths)
    {
        _orchestrator = orchestrator; _downloads = downloads; _startup = startup; _hotkey = hotkey; _updates = updates;
        _audio = audio; _discord = discord; _paths = paths;
        State = orchestrator.State;
        Languages = new(_voiceCatalog.Languages.Where(x => ExposedLanguageIds.Contains(x.Id, StringComparer.OrdinalIgnoreCase)));
        AudioDevices = new(_audio.EnumerateDevices());
        SelectedAudioDevice = AudioDevices.FirstOrDefault(x => x.IsDefault) ?? AudioDevices.FirstOrDefault();
        SelectedVoiceLanguage = Languages.First(x => x.Id == "en-us");
        _hotkey.ToggleRecording += (_, _) => ToggleRecording();
        State.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(State.IsRecording) or nameof(State.LiveTranscript) or nameof(State.RecordingStartedAt))
            {
                OnPropertyChanged(nameof(RecordButtonText));
                OnPropertyChanged(nameof(ElapsedText));
                OnPropertyChanged(nameof(CanCopyCurrent));
                OnPropertyChanged(nameof(CanContinueOnboarding));
            }
        };
        _elapsedTimer = new(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => OnPropertyChanged(nameof(ElapsedText)));
        _elapsedTimer.Start();
        _ = InitializeAsync();
    }

    public MetaVoiceTypeState State { get; }
    public ObservableCollection<VoiceCommandLanguage> Languages { get; }
    public ObservableCollection<AudioDevice> AudioDevices { get; }
    public ObservableCollection<CustomVoiceCommand> CustomCommands { get; } = [];
    public IReadOnlyList<string> DictationLanguages { get; } = ["Automatic", "English"];
    public IReadOnlyList<AppTheme> Themes { get; } = [AppTheme.Dark, AppTheme.Light, AppTheme.System];
    public IReadOnlyList<CustomCommandType> CustomCommandTypes { get; } = Enum.GetValues<CustomCommandType>();
    public IReadOnlyList<CommandWindowMode> WindowModes { get; } = Enum.GetValues<CommandWindowMode>();

    [ObservableProperty] public partial bool ShowOnboarding { get; set; } = true;
    [ObservableProperty] public partial bool ShowSettings { get; set; }
    [ObservableProperty] public partial int OnboardingStep { get; set; } = 1;
    [ObservableProperty] public partial VoiceCommandLanguage SelectedVoiceLanguage { get; set; }
    [ObservableProperty] public partial AudioDevice? SelectedAudioDevice { get; set; }
    [ObservableProperty] public partial string SelectedDictationLanguage { get; set; } = "Automatic";
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool CopyOnStop { get; set; } = true;
    [ObservableProperty] public partial bool ShowFloatingPill { get; set; } = true;
    [ObservableProperty] public partial bool MuteDiscordWhileRecording { get; set; }
    [ObservableProperty] public partial AppTheme Theme { get; set; } = AppTheme.Dark;
    [ObservableProperty] public partial double CueVolume { get; set; } = 0.6;
    [ObservableProperty] public partial double DownloadPercent { get; set; }
    [ObservableProperty] public partial string DownloadStatus { get; set; } = "";
    [ObservableProperty] public partial string DownloadDetail { get; set; } = "";
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial string StartRecordingPhrase { get; set; } = "";
    [ObservableProperty] public partial string StopRecordingPhrase { get; set; } = "";
    [ObservableProperty] public partial string PasteHerePhrase { get; set; } = "";
    [ObservableProperty] public partial string CancelRecordingPhrase { get; set; } = "";
    [ObservableProperty] public partial string CancelPastePhrase { get; set; } = "";
    [ObservableProperty] public partial string CopyPhrase { get; set; } = "";
    [ObservableProperty] public partial string CommandValidation { get; set; } = "";
    [ObservableProperty] public partial string UpdateStatus { get; set; } = "Not checked";
    [ObservableProperty] public partial bool UpdateAvailable { get; set; }
    [ObservableProperty] public partial string HotkeyGesture { get; set; } = "Ctrl+Space";
    [ObservableProperty] public partial string HotkeyValidation { get; set; } = "";
    [ObservableProperty] public partial bool IsCapturingHotkey { get; set; }
    [ObservableProperty] public partial bool IsCapturingCustomShortcut { get; set; }
    [ObservableProperty] public partial bool PillExpanded { get; set; }
    [ObservableProperty] public partial CustomVoiceCommand? SelectedCustomCommand { get; set; }
    [ObservableProperty] public partial string CustomCommandValidation { get; set; } = "";

    public bool IsWelcomeStep => OnboardingStep == 1;
    public bool IsVoiceStep => OnboardingStep == 2;
    public bool IsVoiceDownloadStep => OnboardingStep == 3;
    public bool IsDictationStep => OnboardingStep == 4;
    public bool IsMicrophoneStep => OnboardingStep == 5;
    public bool IsStartupStep => OnboardingStep == 6;
    public bool IsReadyStep => OnboardingStep == 7;
    public bool CanContinueOnboarding => OnboardingStep switch
    {
        3 => _orchestrator.ActiveVoiceCommandLanguageId == SelectedVoiceLanguage?.Id,
        4 => _orchestrator.IsTranscriptionReady,
        _ => true
    };
    public string RecordButtonText => State.IsRecording ? "Stop recording" : "Start recording";
    public string ElapsedText => State.RecordingStartedAt is DateTimeOffset started ? FormatElapsed(DateTimeOffset.UtcNow - started) : "00:00";
    public bool CanCopyCurrent => !string.IsNullOrWhiteSpace(State.LiveTranscript) || State.History.Count > 0;
    public string VoiceLanguageStatus => SelectedVoiceLanguage.Id == State.ActiveVoiceLanguageId ? $"{SelectedVoiceLanguage.DisplayName} · Active"
        : State.VoiceModelState == "Downloading" ? $"{SelectedVoiceLanguage.DisplayName} · Downloading" : $"{SelectedVoiceLanguage.DisplayName} · Not active";
    public string CurrentListenerStatus => State.ActiveVoiceLanguageId is null ? "No command listener" : $"Current listener: {Languages.FirstOrDefault(x => x.Id == State.ActiveVoiceLanguageId)?.DisplayName ?? State.ActiveVoiceLanguageId}";
    public string DiscordStatus => _discord.Status;
    public string DiagnosticsSummary => $"ASR: {State.EngineLabel}\nProvider: {State.Acceleration}\nVosk: {State.ActiveVoiceLanguageId ?? "not active"}\nMicrophone: {SelectedAudioDevice?.Name ?? "unavailable"}";

    private async Task InitializeAsync()
    {
        await _orchestrator.InitializeAsync();
        AppSettings settings = _orchestrator.Settings;
        SelectedVoiceLanguage = Languages.FirstOrDefault(x => x.Id == settings.VoiceCommandLanguage) ?? Languages[0];
        SelectedAudioDevice = AudioDevices.FirstOrDefault(x => x.Id == settings.AudioDeviceId) ?? AudioDevices.FirstOrDefault(x => x.IsDefault) ?? AudioDevices.FirstOrDefault();
        SelectedDictationLanguage = settings.DictationMode == DictationMode.English ? "English" : "Automatic";
        StartWithWindows = settings.StartWithWindows; CopyOnStop = settings.CopyOnStop; ShowFloatingPill = settings.ShowFloatingPill;
        MuteDiscordWhileRecording = settings.MuteDiscordWhileRecording; Theme = settings.Theme; CueVolume = settings.CueVolume; HotkeyGesture = settings.ToggleHotkey;
        foreach (CustomVoiceCommand command in settings.CustomCommands) CustomCommands.Add(command);
        ApplyTheme(Theme);
        ShowOnboarding = !settings.OnboardingComplete;
        LoadPhrases();
        await TryInitializeInstalledModelsAsync();
        await _hotkey.StartAsync(HotkeyGesture);
        if (settings.CheckForUpdates) await CheckForUpdatesAsync();
        _loading = false;
        RefreshComputedStatus();
    }

    partial void OnOnboardingStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsWelcomeStep)); OnPropertyChanged(nameof(IsVoiceStep)); OnPropertyChanged(nameof(IsVoiceDownloadStep));
        OnPropertyChanged(nameof(IsDictationStep)); OnPropertyChanged(nameof(IsMicrophoneStep)); OnPropertyChanged(nameof(IsStartupStep)); OnPropertyChanged(nameof(IsReadyStep));
        OnPropertyChanged(nameof(CanContinueOnboarding));
    }
    partial void OnSelectedVoiceLanguageChanged(VoiceCommandLanguage value)
    {
        if (value is null) return;
        State.SelectedVoiceLanguageId = value.Id;
        LoadPhrases();
        RefreshComputedStatus();
    }
    partial void OnThemeChanged(AppTheme value) { if (!_loading) ApplyTheme(value); }
    partial void OnStartRecordingPhraseChanged(string value) => ScheduleCommandSave();
    partial void OnStopRecordingPhraseChanged(string value) => ScheduleCommandSave();
    partial void OnPasteHerePhraseChanged(string value) => ScheduleCommandSave();
    partial void OnCancelRecordingPhraseChanged(string value) => ScheduleCommandSave();
    partial void OnCancelPastePhraseChanged(string value) => ScheduleCommandSave();
    partial void OnCopyPhraseChanged(string value) => ScheduleCommandSave();

    private void LoadPhrases()
    {
        if (SelectedVoiceLanguage is null) return;
        _loading = true;
        IReadOnlyDictionary<VoiceCommand, string> values = _orchestrator.ResolvePhrases(SelectedVoiceLanguage);
        StartRecordingPhrase = values[VoiceCommand.StartRecording]; StopRecordingPhrase = values[VoiceCommand.StopRecording]; PasteHerePhrase = values[VoiceCommand.PasteHere];
        CancelRecordingPhrase = values[VoiceCommand.CancelRecording]; CancelPastePhrase = values[VoiceCommand.CancelPaste]; CopyPhrase = values[VoiceCommand.CopyRecordingToClipboard];
        _loading = false;
    }

    private Dictionary<VoiceCommand, string> CurrentPhrases() => new()
    {
        [VoiceCommand.StartRecording] = StartRecordingPhrase, [VoiceCommand.StopRecording] = StopRecordingPhrase, [VoiceCommand.PasteHere] = PasteHerePhrase,
        [VoiceCommand.CancelRecording] = CancelRecordingPhrase, [VoiceCommand.CancelPaste] = CancelPastePhrase, [VoiceCommand.CopyRecordingToClipboard] = CopyPhrase
    };

    [RelayCommand] private void NextOnboarding() { if (CanContinueOnboarding && OnboardingStep < 7) OnboardingStep++; }
    [RelayCommand] private void PreviousOnboarding() { if (OnboardingStep > 1) OnboardingStep--; }
    [RelayCommand] private void ToggleSettings() => ShowSettings = !ShowSettings;
    [RelayCommand] private void ToggleRecording() { if (IsCapturingHotkey || IsCapturingCustomShortcut) return; if (State.IsRecording) _orchestrator.StopRecording(); else _orchestrator.StartRecording(); }
    [RelayCommand] private void StopRecording() => _orchestrator.StopRecording();
    [RelayCommand] private void Paste() => _orchestrator.PasteHere();
    [RelayCommand] private void CancelRecording() => _orchestrator.StopRecording(canceled: true);
    [RelayCommand] private Task CopyAsync() => _orchestrator.CopyCurrentAsync();
    [RelayCommand] private Task CopyHistoryAsync(TranscriptRecord record) => _orchestrator.CopyRecordAsync(record);
    [RelayCommand] private void PasteHistory(TranscriptRecord record) => _orchestrator.PasteRecord(record);
    [RelayCommand] private void CancelDownload() => _downloadCancellation?.Cancel();

    [RelayCommand]
    private async Task DownloadVoiceModelAsync()
    {
        await RunDownloadAsync(async token =>
        {
            State.VoiceModelState = "Downloading";
            string path = await _downloads.InstallAsync(new(SelectedVoiceLanguage.ArchiveUrl, "zip", SelectedVoiceLanguage.ModelName, _paths.VoskModels, null,
                SelectedVoiceLanguage.SizeBytes, ["am/final.mdl", "conf/mfcc.conf"]), Progress(SelectedVoiceLanguage.DisplayName), token);
            State.VoiceModelState = "Initializing";
            _orchestrator.InitializeVosk(path, SelectedVoiceLanguage);
            await _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { VoiceCommandLanguage = SelectedVoiceLanguage.Id }, token);
            DownloadStatus = $"{SelectedVoiceLanguage.DisplayName} is active";
            RefreshComputedStatus();
        });
    }

    [RelayCommand]
    private async Task DownloadDictationAssetsAsync()
    {
        await RunDownloadAsync(async token =>
        {
            string modelId = SelectedDictationLanguage == "English" ? "parakeet-v2" : "parakeet-v3";
            ModelArtifact model = _modelCatalog.Get(modelId);
            State.DictationModelState = "Downloading";
            if (_orchestrator.HasNvidiaGpu) try
            {
                ModelArtifact runtime = _modelCatalog.Get("sherpa-cuda-12");
                await _downloads.InstallAsync(runtime.ToInstallRequest(_paths.RuntimeModels), Progress("NVIDIA GPU runtime"), token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                State.ProviderFallbackReason = "GPU runtime download failed; CPU remains available: " + ex.Message;
            }
            else State.ProviderFallbackReason = "No compatible NVIDIA GPU was detected; CPU will be used.";
            ModelArtifact vad = _modelCatalog.Get("silero-vad");
            string vadPath = await _downloads.InstallAsync(vad.ToInstallRequest(_paths.DictationModels), Progress("Silero VAD"), token);
            string modelPath = await _downloads.InstallAsync(model.ToInstallRequest(_paths.DictationModels), Progress(model.DisplayName), token);
            State.DictationModelState = "Initializing";
            await _orchestrator.InitializeParakeetAsync(modelPath, model, vadPath, token);
            DictationMode mode = modelId == "parakeet-v2" ? DictationMode.English : DictationMode.Automatic;
            await _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { DictationMode = mode }, token);
            DownloadStatus = $"{model.DisplayName} is ready";
            RefreshComputedStatus();
        });
    }

    private async Task RunDownloadAsync(Func<CancellationToken, Task> operation)
    {
        if (IsDownloading) return;
        _downloadCancellation = new();
        IsDownloading = true; DownloadPercent = 0; DownloadDetail = "";
        try { await operation(_downloadCancellation.Token); }
        catch (OperationCanceledException) { DownloadStatus = "Download canceled"; }
        catch (Exception ex) { DownloadStatus = "Download failed: " + ex.Message; State.VoiceModelState = State.VoiceModelState == "Downloading" ? "Failed" : State.VoiceModelState; State.DictationModelState = State.DictationModelState == "Downloading" ? "Failed" : State.DictationModelState; }
        finally { IsDownloading = false; _downloadCancellation.Dispose(); _downloadCancellation = null; RefreshComputedStatus(); }
    }

    private Progress<ModelDownloadProgress> Progress(string name) => new(value => Dispatcher.UIThread.Post(() =>
    {
        DownloadPercent = value.Percentage ?? 0;
        DownloadStatus = $"{name} · {value.Stage}";
        DownloadDetail = value.TotalBytes is long total ? $"{FormatBytes(value.BytesDownloaded)} / {FormatBytes(total)}" : FormatBytes(value.BytesDownloaded);
    }));

    [RelayCommand]
    private async Task ApplySettingsAsync()
    {
        try
        {
            HotkeyChangeResult hotkey = await _hotkey.ChangeAsync(HotkeyGesture);
            if (!hotkey.Success) { HotkeyValidation = hotkey.Error ?? "Shortcut could not be activated."; HotkeyGesture = hotkey.ActiveGesture; return; }
            HotkeyGesture = hotkey.ActiveGesture; HotkeyValidation = "";
            DictationMode mode = SelectedDictationLanguage == "English" ? DictationMode.English : DictationMode.Automatic;
            var settings = _orchestrator.Settings with
            {
                VoiceCommandLanguage = SelectedVoiceLanguage.Id, DictationMode = mode, AudioDeviceId = SelectedAudioDevice?.Id,
                StartWithWindows = StartWithWindows, CopyOnStop = CopyOnStop, ShowFloatingPill = ShowFloatingPill,
                MuteDiscordWhileRecording = MuteDiscordWhileRecording, Theme = Theme, CueVolume = CueVolume, ToggleHotkey = HotkeyGesture
            };
            await _orchestrator.UpdateSettingsAsync(settings);
            _startup.SetEnabled(StartWithWindows);
            ApplyTheme(Theme);
            CommandValidation = "Saved";
            RefreshComputedStatus();
        }
        catch (Exception ex) { CommandValidation = ex.Message; }
    }

    [RelayCommand]
    private async Task FinishOnboardingAsync()
    {
        await ApplySettingsAsync();
        if (!_orchestrator.IsTranscriptionReady || _orchestrator.ActiveVoiceCommandLanguageId is null)
        {
            DownloadStatus = "Install and initialize both models before finishing setup";
            return;
        }
        await _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { OnboardingComplete = true });
        ShowOnboarding = false;
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            if (!_updates.IsInstalled) { UpdateStatus = "Updates are available after installation"; UpdateAvailable = false; return; }
            UpdateStatus = "Checking…";
            string? version = await _updates.CheckAsync();
            UpdateAvailable = version is not null;
            UpdateStatus = version is null ? "Up to date" : $"Version {version} is available";
        }
        catch (Exception ex) { UpdateAvailable = false; UpdateStatus = "Update check failed: " + ex.Message; }
    }

    [RelayCommand] private async Task UpdateNowAsync() { await _updates.DownloadAndRestartAsync(); }
    [RelayCommand] private void ResetCommands() { _loading = true; StartRecordingPhrase = SelectedVoiceLanguage.Commands["startRecording"]; StopRecordingPhrase = SelectedVoiceLanguage.Commands["stopRecording"]; PasteHerePhrase = SelectedVoiceLanguage.Commands["pasteHere"]; CancelRecordingPhrase = SelectedVoiceLanguage.Commands["cancelRecording"]; CancelPastePhrase = SelectedVoiceLanguage.Commands["cancelPaste"]; CopyPhrase = SelectedVoiceLanguage.Commands["copyRecordingToClipboard"]; _loading = false; ScheduleCommandSave(); }
    [RelayCommand] private void BeginHotkeyCapture() { IsCapturingHotkey = true; HotkeyValidation = "Press a shortcut…"; }
    [RelayCommand] private void BeginCustomShortcutCapture() { if (SelectedCustomCommand is not null) { IsCapturingCustomShortcut = true; CustomCommandValidation = "Press a shortcut…"; } }
    [RelayCommand] private async Task ResetHotkeyAsync() { HotkeyGesture = "Ctrl+Space"; HotkeyChangeResult result = await _hotkey.ChangeAsync(HotkeyGesture); HotkeyValidation = result.Error ?? ""; }

    public async Task CaptureHotkeyAsync(string gesture)
    {
        HotkeyChangeResult result = await _hotkey.ChangeAsync(gesture);
        IsCapturingHotkey = false;
        HotkeyGesture = result.ActiveGesture;
        HotkeyValidation = result.Success ? "" : result.Error ?? "Shortcut could not be activated.";
        if (result.Success) await _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { ToggleHotkey = result.ActiveGesture });
    }

    public void CaptureCustomShortcut(string gesture)
    {
        IsCapturingCustomShortcut = false;
        if (SelectedCustomCommand is null) return;
        try
        {
            SelectedCustomCommand.Shortcut = ShortcutGestureParser.Parse(gesture).ToString();
            CustomCommandValidation = "Shortcut captured. Save to apply.";
            OnPropertyChanged(nameof(SelectedCustomCommand));
        }
        catch (FormatException ex) { CustomCommandValidation = ex.Message; }
    }

    [RelayCommand]
    private void AddCustomCommand()
    {
        var command = new CustomVoiceCommand { VoiceCommandLanguageId = SelectedVoiceLanguage.Id, Name = "New command", CommandType = CustomCommandType.Program };
        CustomCommands.Add(command); SelectedCustomCommand = command;
    }

    [RelayCommand]
    private async Task SaveCustomCommandAsync()
    {
        if (SelectedCustomCommand is null) return;
        try
        {
            VoiceCommandLanguage commandLanguage = _voiceCatalog.Get(SelectedCustomCommand.VoiceCommandLanguageId);
            CustomCommandValidator.Validate(SelectedCustomCommand, _orchestrator.ResolvePhrases(commandLanguage).Values, CustomCommands);
            var commands = CustomCommands.ToList();
            await _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { CustomCommands = commands });
            CustomCommandValidation = "Saved";
        }
        catch (Exception ex) { CustomCommandValidation = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteCustomCommandAsync(CustomVoiceCommand command)
    {
        CustomCommands.Remove(command);
        if (ReferenceEquals(SelectedCustomCommand, command)) SelectedCustomCommand = null;
        await _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { CustomCommands = CustomCommands.ToList() });
    }

    private void ScheduleCommandSave()
    {
        if (_loading || SelectedVoiceLanguage is null) return;
        _commandSaveDebounce?.Cancel(); _commandSaveDebounce?.Dispose();
        _commandSaveDebounce = new();
        CancellationToken token = _commandSaveDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(450, token);
                await _orchestrator.UpdateCommandPhrasesAsync(SelectedVoiceLanguage.Id, CurrentPhrases(), token);
                Dispatcher.UIThread.Post(() => CommandValidation = "Saved");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Dispatcher.UIThread.Post(() => CommandValidation = ex.Message); }
        }, CancellationToken.None);
    }

    private async Task TryInitializeInstalledModelsAsync()
    {
        string voicePath = Path.Combine(_paths.VoskModels, SelectedVoiceLanguage.ModelName);
        if (File.Exists(Path.Combine(voicePath, "am", "final.mdl")))
        {
            try { _orchestrator.InitializeVosk(voicePath, SelectedVoiceLanguage); }
            catch (Exception ex) { DownloadStatus = "Voice model initialization failed: " + ex.Message; }
        }
        string modelId = SelectedDictationLanguage == "English" ? "parakeet-v2" : "parakeet-v3";
        ModelArtifact model = _modelCatalog.Get(modelId); ModelArtifact vad = _modelCatalog.Get("silero-vad");
        string modelPath = Path.Combine(_paths.DictationModels, model.ExpectedDirectory);
        string vadPath = Path.Combine(_paths.DictationModels, vad.ExpectedDirectory);
        if (IsInstalled(modelPath, model) && IsInstalled(vadPath, vad))
        {
            try { await _orchestrator.InitializeParakeetAsync(modelPath, model, vadPath); }
            catch (Exception ex) { DownloadStatus = "Dictation initialization failed: " + ex.Message; }
        }
    }

    private static bool IsInstalled(string directory, ModelArtifact artifact) => artifact.RequiredFiles.All(file => File.Exists(Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar))));
    private static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch { AppTheme.Light => ThemeVariant.Light, AppTheme.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }
    private void RefreshComputedStatus()
    {
        OnPropertyChanged(nameof(VoiceLanguageStatus)); OnPropertyChanged(nameof(CurrentListenerStatus)); OnPropertyChanged(nameof(DiscordStatus)); OnPropertyChanged(nameof(DiagnosticsSummary));
        OnPropertyChanged(nameof(CanContinueOnboarding));
    }
    private static string FormatBytes(long bytes) => bytes switch { >= 1_073_741_824 => $"{bytes / 1_073_741_824d:F1} GB", >= 1_048_576 => $"{bytes / 1_048_576d:F1} MB", >= 1024 => $"{bytes / 1024d:F1} KB", _ => $"{bytes} B" };
    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture) : elapsed.ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);

    public bool ShouldShowCloseToTrayNotice => !_orchestrator.Settings.CloseToTrayNoticeShown;
    public Task MarkCloseToTrayNoticeShownAsync() => _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { CloseToTrayNoticeShown = true });

    public void Dispose()
    {
        _elapsedTimer.Stop();
        _downloadCancellation?.Cancel(); _downloadCancellation?.Dispose(); _downloadCancellation = null;
        _commandSaveDebounce?.Cancel(); _commandSaveDebounce?.Dispose(); _commandSaveDebounce = null;
        GC.SuppressFinalize(this);
    }
}
