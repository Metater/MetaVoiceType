using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Audio;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Core.State;
using MetaVoiceType.Diagnostics;
using MetaVoiceType.Models;
using MetaVoiceType.Sessions;
using MetaVoiceType.Storage;
using MetaVoiceType.VoiceCommands;

namespace MetaVoiceType.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    public enum ShortcutCaptureTarget { None, RecordingToggle, CustomCommand, RecordingStarted, RecordingStopped }
    private static readonly string[] ExposedLanguageIds = ["en-us", "ru", "fr", "de", "es", "pt-br", "it", "nl", "uk", "sv", "cs", "pl"];
    private readonly ApplicationOrchestrator _orchestrator;
    private readonly IModelDownloadService _downloads;
    private readonly IStartupService _startup;
    private readonly IGlobalHotkeyService _hotkey;
    private readonly GlobalHotkeyRegistration _globalHotkey;
    private readonly IUpdateService _updates;
    private readonly IAudioCaptureService _audio;
    private readonly AudioSpectrumService _spectrum;
    private readonly IAudioCueService _cues;
    private readonly AppPaths _paths;
    private readonly StartupOptions _startupOptions;
    private readonly VoiceCommandCatalog _voiceCatalog = VoiceCommandCatalog.LoadBundled();
    private readonly ModelCatalog _modelCatalog = ModelCatalog.LoadBundled();
    private readonly DispatcherTimer _elapsedTimer;
    private CancellationTokenSource? _downloadCancellation;
    private CancellationTokenSource? _commandSaveDebounce;
    private CancellationTokenSource? _deleteConfirmationTimeout;
    private bool _loading = true;
    private string? _lastDownloadKind;
    private int _animationPhase;

    public MainViewModel(ApplicationOrchestrator orchestrator, IModelDownloadService downloads,
        IStartupService startup, IGlobalHotkeyService hotkey, GlobalHotkeyRegistration globalHotkey, IUpdateService updates, IAudioCaptureService audio,
        AudioSpectrumService spectrum, IAudioCueService cues, AppPaths paths, StartupOptions startupOptions)
    {
        _orchestrator = orchestrator; _downloads = downloads; _startup = startup; _hotkey = hotkey; _globalHotkey = globalHotkey; _updates = updates;
        _audio = audio; _spectrum = spectrum; _cues = cues; _paths = paths; _startupOptions = startupOptions;
        State = orchestrator.State;
        Languages = new(_voiceCatalog.Languages.Where(x => ExposedLanguageIds.Contains(x.Id, StringComparer.OrdinalIgnoreCase)));
        AudioDevices = new(_audio.EnumerateDevices());
        SelectedAudioDevice = AudioDevices.FirstOrDefault(x => x.IsDefault) ?? AudioDevices.FirstOrDefault();
        BuiltInAliasEditors = new([
            new(VoiceCommand.StartRecording, "Start recording"), new(VoiceCommand.ContinueRecording, "Continue recording"),
            new(VoiceCommand.StopRecording, "Stop recording"), new(VoiceCommand.PasteRecording, "Paste Recording"),
            new(VoiceCommand.CancelRecording, "Cancel recording"), new(VoiceCommand.CancelPaste, "Cancel paste"),
            new(VoiceCommand.CopyRecordingToClipboard, "Copy recording")]);
        foreach (CommandAliasEditorViewModel editor in BuiltInAliasEditors) editor.Aliases.Changed += (_, _) => ScheduleCommandSave();
        SelectedVoiceLanguage = Languages.First(x => x.Id == "en-us");
        _spectrum.FrameReady += OnSpectrumFrame;
        State.History.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasHistory)); OnPropertyChanged(nameof(CanCopyCurrent)); };
        _hotkey.ToggleRecording += (_, _) =>
        {
            if (_orchestrator.Readiness.CanUseGlobalRecordingShortcut) ToggleRecording();
        };
        State.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(State.IsRecording) or nameof(State.LiveTranscript) or nameof(State.RecordingStartedAt) or nameof(State.ActiveVoiceLanguageId) or nameof(State.PasteState))
            {
                OnPropertyChanged(nameof(RecordButtonText));
                OnPropertyChanged(nameof(ElapsedText));
                OnPropertyChanged(nameof(CanCopyCurrent));
                OnPropertyChanged(nameof(CanContinueOnboarding));
                OnPropertyChanged(nameof(MainCommandText));
                OnPropertyChanged(nameof(PillStatusText)); OnPropertyChanged(nameof(IsPasteOnly));
                OnPropertyChanged(nameof(PasteStatusText)); OnPropertyChanged(nameof(ShowPasteActivity));
                OnPropertyChanged(nameof(CanStartRecording)); OnPropertyChanged(nameof(CanStopRecording)); OnPropertyChanged(nameof(CanPaste));
            }
            if (args.PropertyName is nameof(State.VoiceModelState) or nameof(State.DictationModelState) or nameof(State.Acceleration)) RefreshComputedStatus();
        };
        _orchestrator.Readiness.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(CanStartRecording)); OnPropertyChanged(nameof(CanStopRecording)); OnPropertyChanged(nameof(CanPaste));
            OnPropertyChanged(nameof(VoiceModelActionText)); OnPropertyChanged(nameof(DiagnosticsSummary)); OnPropertyChanged(nameof(CanContinueOnboarding));
        });
        _elapsedTimer = new(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _animationPhase = (_animationPhase + 1) % 4;
            OnPropertyChanged(nameof(ElapsedText)); OnPropertyChanged(nameof(AnimatedStatusText)); OnPropertyChanged(nameof(PillStatusText));
        });
        _elapsedTimer.Start();
        _ = InitializeAsync();
    }

    public MetaVoiceTypeState State { get; }
    public ObservableCollection<VoiceCommandLanguage> Languages { get; }
    public ObservableCollection<AudioDevice> AudioDevices { get; }
    public ObservableCollection<CustomVoiceCommand> CustomCommands { get; } = [];
    public ObservableCollection<ReplacementGroupEditorViewModel> ReplacementGroups { get; } = [];
    public ObservableCollection<CommandAliasEditorViewModel> BuiltInAliasEditors { get; }
    public AliasListEditorViewModel CustomAliases { get; } = new();
    public IReadOnlyList<string> DictationLanguages { get; } = ["Automatic", "English"];
    public IReadOnlyList<AppTheme> Themes { get; } = [AppTheme.System, AppTheme.Dark, AppTheme.Light];
    public IReadOnlyList<CustomCommandType> CustomCommandTypes { get; } = Enum.GetValues<CustomCommandType>();
    public IReadOnlyList<CommandWindowMode> WindowModes { get; } = Enum.GetValues<CommandWindowMode>();

    [ObservableProperty] public partial bool ShowOnboarding { get; set; } = true;
    [ObservableProperty] public partial bool ShowSettings { get; set; }
    [ObservableProperty] public partial int SettingsTabIndex { get; set; }
    [ObservableProperty] public partial int OnboardingStep { get; set; } = 1;
    [ObservableProperty] public partial VoiceCommandLanguage SelectedVoiceLanguage { get; set; }
    [ObservableProperty] public partial AudioDevice? SelectedAudioDevice { get; set; }
    [ObservableProperty] public partial string SelectedDictationLanguage { get; set; } = "Automatic";
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool CopyOnStop { get; set; } = true;
    [ObservableProperty] public partial bool ShowFloatingPill { get; set; } = true;
    [ObservableProperty] public partial bool ForceCpuOnly { get; set; }
    [ObservableProperty] public partial AppTheme Theme { get; set; } = AppTheme.System;
    [ObservableProperty] public partial double CueVolume { get; set; } = 0.6;
    [ObservableProperty] public partial double DownloadPercent { get; set; }
    [ObservableProperty] public partial string DownloadStatus { get; set; } = "";
    [ObservableProperty] public partial string DownloadDetail { get; set; } = "";
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial bool DownloadFailed { get; set; }
    [ObservableProperty] public partial bool ShowDeleteAllConfirmation { get; set; }
    [ObservableProperty] public partial string StartRecordingPhrase { get; set; } = "";
    [ObservableProperty] public partial string ContinueRecordingPhrase { get; set; } = "";
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
    [ObservableProperty] public partial ShortcutCaptureTarget ActiveShortcutCapture { get; set; }
    [ObservableProperty] public partial string RecordingStartedShortcut { get; set; } = "";
    [ObservableProperty] public partial string RecordingStoppedShortcut { get; set; } = "";
    [ObservableProperty] public partial CustomVoiceCommand? SelectedCustomCommand { get; set; }
    [ObservableProperty] public partial CustomCommandType SelectedCustomCommandType { get; set; }
    [ObservableProperty] public partial TranscriptRecord? PendingDeleteRecord { get; set; }
    [ObservableProperty] public partial string CustomCommandValidation { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<double> SpectrumBars { get; set; } = new double[AudioSpectrumService.BarCount];

    public bool IsCapturingHotkey => ActiveShortcutCapture == ShortcutCaptureTarget.RecordingToggle;
    public bool IsCapturingCustomShortcut => ActiveShortcutCapture == ShortcutCaptureTarget.CustomCommand;
    public bool IsCapturingRecordingStartedShortcut => ActiveShortcutCapture == ShortcutCaptureTarget.RecordingStarted;
    public bool IsCapturingRecordingStoppedShortcut => ActiveShortcutCapture == ShortcutCaptureTarget.RecordingStopped;
    public bool HasSelectedCustomCommand => SelectedCustomCommand is not null;
    public bool ShowProgramFields => HasSelectedCustomCommand && SelectedCustomCommandType == CustomCommandType.Program;
    public bool ShowScriptFields => HasSelectedCustomCommand && SelectedCustomCommandType is CustomCommandType.PowerShell or CustomCommandType.CommandPrompt;
    public bool ShowShortcutFields => HasSelectedCustomCommand && SelectedCustomCommandType == CustomCommandType.KeyboardShortcut;

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
        5 => SelectedAudioDevice is not null && _orchestrator.Readiness.MicrophoneReady,
        _ => true
    };
    public string RecordButtonText => State.IsRecording ? "Stop recording" : "Start recording";
    public bool IsDeleteConfirmationVisible => PendingDeleteRecord is not null;
    public string MainCommandText
    {
        get
        {
            string? id = State.ActiveVoiceLanguageId;
            if (id is null) return "Voice command model not active";
            VoiceCommandLanguage language = _voiceCatalog.Get(id);
            return VoiceCommandCopy.ForRecordingState(State.IsRecording, _orchestrator.ResolveAliases(language));
        }
    }
    public string AnimatedStatusText => Animate(State.StatusMessage);
    public string PillStatusText => State.IsRecording ? Animate("Recording") : State.PasteState switch
    {
        PasteRequestState.Queued => Animate("Preparing"),
        PasteRequestState.Preparing => Animate("Preparing"),
        PasteRequestState.Pasting => Animate("Pasting"),
        PasteRequestState.Succeeded => "Pasted",
        PasteRequestState.Failed => "Paste failed",
        PasteRequestState.Canceled => "Paste canceled",
        _ => "Ready"
    };
    public bool IsPasteOnly => !State.IsRecording && State.IsPasteActive;
    public bool ShowPasteActivity => State.IsPasteActive;
    public string PasteStatusText => State.PasteState == PasteRequestState.Pasting ? Animate("Pasting") : Animate("Preparing");
    public bool CanStartRecording => _orchestrator.Readiness.CanRecord && !State.IsRecording;
    public bool CanStopRecording => _orchestrator.Readiness.CanRecord && State.IsRecording;
    public bool CanPaste => _orchestrator.Readiness.CanPaste && !State.IsPasteActive;
    public string ElapsedText => State.RecordingStartedAt is DateTimeOffset started ? FormatElapsed(DateTimeOffset.UtcNow - started) : "00:00";
    public bool CanCopyCurrent => !string.IsNullOrWhiteSpace(State.LiveTranscript) || State.History.Count > 0;
    public bool HasHistory => State.History.Count > 0;
    public string VoiceLanguageStatus => SelectedVoiceLanguage.Id == State.ActiveVoiceLanguageId ? $"{SelectedVoiceLanguage.DisplayName} · Active"
        : State.VoiceModelState == "Downloading" ? $"{SelectedVoiceLanguage.DisplayName} · Downloading {DownloadPercent:F0}%" : $"{SelectedVoiceLanguage.DisplayName} · Not active";
    public string CurrentListenerStatus => State.ActiveVoiceLanguageId is null ? "Voice command language: Not active" : $"Voice command language: {Languages.FirstOrDefault(x => x.Id == State.ActiveVoiceLanguageId)?.DisplayName ?? State.ActiveVoiceLanguageId}";
    public string DiagnosticsSummary => $"ASR: {State.EngineLabel}\nProvider: {State.Acceleration}\nVosk: {State.ActiveVoiceLanguageId ?? "not active"}\nMicrophone: {SelectedAudioDevice?.Name ?? "unavailable"}";
    public string VoiceModelActionText => _orchestrator.Readiness.SetupCompletedOnce && !_orchestrator.Readiness.VoiceCommandsReady
        ? "Repair command model" : "Install command model";
    public string ParakeetV2Status => IsInstalledArtifact("parakeet-v2") ? "Installed" : "Not installed";
    public string ParakeetV3Status => IsInstalledArtifact("parakeet-v3") ? "Installed" : "Not installed";
    public string AccelerationStatus => State.Acceleration is "Not installed" ? "CPU fallback available" : State.Acceleration;
    public bool ShowNvidiaMark => State.Acceleration == "GPU" && _orchestrator.HasNvidiaGpu;

    private async Task InitializeAsync()
    {
        await _orchestrator.InitializeAsync();
        AppSettings settings = _orchestrator.Settings;
        SelectedVoiceLanguage = Languages.FirstOrDefault(x => x.Id == settings.VoiceCommandLanguage) ?? Languages[0];
        SelectedAudioDevice = AudioDevices.FirstOrDefault(x => x.Id == settings.AudioDeviceId) ?? AudioDevices.FirstOrDefault(x => x.IsDefault) ?? AudioDevices.FirstOrDefault();
        SelectedDictationLanguage = settings.DictationMode == DictationMode.English ? "English" : "Automatic";
        StartWithWindows = settings.StartWithWindows; CopyOnStop = settings.CopyOnStop; ShowFloatingPill = settings.ShowFloatingPill; ForceCpuOnly = settings.ForceCpuOnly;
        Theme = settings.Theme; CueVolume = settings.CueVolume; HotkeyGesture = settings.ToggleHotkey;
        RecordingStartedShortcut = settings.RecordingStartedShortcut ?? "";
        RecordingStoppedShortcut = settings.RecordingStoppedShortcut ?? "";
        foreach (CustomVoiceCommand command in settings.CustomCommands) CustomCommands.Add(command);
        foreach (WordReplacementGroup group in settings.WordReplacementGroups) ReplacementGroups.Add(new(group));
        ApplyTheme(Theme);
        ShowOnboarding = !settings.SetupCompletedOnce;
        LoadPhrases();
        await TryInitializeInstalledModelsAsync();
        _orchestrator.CompleteStartupReadiness();
        await _globalHotkey.ApplyAsync(HotkeyGesture);
        if (settings.CheckForUpdates) await CheckForUpdatesAsync();
        _loading = false;
        RefreshComputedStatus();
        if (_startupOptions.UiView is string view && (view.Equals("settings", StringComparison.OrdinalIgnoreCase) || view.StartsWith("settings-", StringComparison.OrdinalIgnoreCase)))
        {
            ShowSettings = true;
            SettingsTabIndex = view.ToLowerInvariant() switch
            {
                "settings-voice" => 1,
                "settings-custom" => 2,
                "settings-replacements" => 3,
                "settings-audio" => 4,
                "settings-about" => 5,
                _ => 0
            };
            if (SettingsTabIndex == 2 && CustomCommands.Count > 0) SelectedCustomCommand = CustomCommands[0];
        }
        else if (_startupOptions.UiView?.Equals("pill", StringComparison.OrdinalIgnoreCase) == true)
        {
            State.RecordingStartedAt = DateTimeOffset.UtcNow;
            State.StatusMessage = "Recording";
            State.IsRecording = true;
        }
        else if (_startupOptions.UiView?.Equals("paste-pill", StringComparison.OrdinalIgnoreCase) == true)
            State.PasteState = PasteRequestState.Preparing;
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
    partial void OnSelectedAudioDeviceChanged(AudioDevice? value) => OnPropertyChanged(nameof(CanContinueOnboarding));
    partial void OnSelectedCustomCommandChanged(CustomVoiceCommand? value)
    {
        CustomAliases.Replace(value?.Aliases ?? []);
        SelectedCustomCommandType = value?.CommandType ?? CustomCommandType.Program;
        OnPropertyChanged(nameof(HasSelectedCustomCommand));
        OnPropertyChanged(nameof(ShowProgramFields)); OnPropertyChanged(nameof(ShowScriptFields)); OnPropertyChanged(nameof(ShowShortcutFields));
    }
    partial void OnSelectedCustomCommandTypeChanged(CustomCommandType value)
    {
        if (SelectedCustomCommand is not null) SelectedCustomCommand.CommandType = value;
        OnPropertyChanged(nameof(ShowProgramFields)); OnPropertyChanged(nameof(ShowScriptFields)); OnPropertyChanged(nameof(ShowShortcutFields));
    }
    partial void OnActiveShortcutCaptureChanged(ShortcutCaptureTarget value)
    {
        OnPropertyChanged(nameof(IsCapturingHotkey)); OnPropertyChanged(nameof(IsCapturingCustomShortcut));
        OnPropertyChanged(nameof(IsCapturingRecordingStartedShortcut)); OnPropertyChanged(nameof(IsCapturingRecordingStoppedShortcut));
    }
    partial void OnThemeChanged(AppTheme value) { if (!_loading) ApplyTheme(value); }
    partial void OnDownloadPercentChanged(double value) => OnPropertyChanged(nameof(VoiceLanguageStatus));
    partial void OnPendingDeleteRecordChanged(TranscriptRecord? value) => OnPropertyChanged(nameof(IsDeleteConfirmationVisible));
    partial void OnStartRecordingPhraseChanged(string value) => ScheduleCommandSave();
    partial void OnContinueRecordingPhraseChanged(string value) => ScheduleCommandSave();
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
        IReadOnlyDictionary<VoiceCommand, IReadOnlyList<string>> aliases = _orchestrator.ResolveAliases(SelectedVoiceLanguage);
        foreach (CommandAliasEditorViewModel editor in BuiltInAliasEditors) editor.Aliases.Replace(aliases[editor.Command]);
        StartRecordingPhrase = values[VoiceCommand.StartRecording]; ContinueRecordingPhrase = values[VoiceCommand.ContinueRecording]; StopRecordingPhrase = values[VoiceCommand.StopRecording]; PasteHerePhrase = values[VoiceCommand.PasteRecording];
        CancelRecordingPhrase = values[VoiceCommand.CancelRecording]; CancelPastePhrase = values[VoiceCommand.CancelPaste]; CopyPhrase = values[VoiceCommand.CopyRecordingToClipboard];
        _loading = false;
    }

    private Dictionary<VoiceCommand, IReadOnlyList<string>> CurrentAliases() => BuiltInAliasEditors
        .ToDictionary(x => x.Command, x => (IReadOnlyList<string>)x.Aliases.Values);

    private Dictionary<VoiceCommand, string> CurrentPhrases() => new()
    {
        [VoiceCommand.StartRecording] = StartRecordingPhrase,
        [VoiceCommand.ContinueRecording] = ContinueRecordingPhrase,
        [VoiceCommand.StopRecording] = StopRecordingPhrase,
        [VoiceCommand.PasteRecording] = PasteHerePhrase,
        [VoiceCommand.CancelRecording] = CancelRecordingPhrase,
        [VoiceCommand.CancelPaste] = CancelPastePhrase,
        [VoiceCommand.CopyRecordingToClipboard] = CopyPhrase
    };

    [RelayCommand] private void NextOnboarding() { if (CanContinueOnboarding && OnboardingStep < 7) OnboardingStep++; }
    [RelayCommand] private void PreviousOnboarding() { if (OnboardingStep > 1) OnboardingStep--; }
    [RelayCommand] private void ToggleSettings() => ShowSettings = !ShowSettings;
    [RelayCommand] private void ToggleRecording() { if (ActiveShortcutCapture != ShortcutCaptureTarget.None) return; if (State.IsRecording) _orchestrator.StopRecording(); else _orchestrator.StartRecording(); }
    [RelayCommand] private void StopRecording() => _orchestrator.StopRecording();
    [RelayCommand] private void ContinueRecording() => _orchestrator.ContinueRecording();
    [RelayCommand] private void Paste() => _orchestrator.PasteHere();
    [RelayCommand] private void CancelPaste() => _orchestrator.CancelPaste();
    [RelayCommand] private void CancelRecording() => _orchestrator.StopRecording(canceled: true);
    [RelayCommand] private Task CopyAsync() => _orchestrator.CopyCurrentAsync();
    [RelayCommand] private Task CopyHistoryAsync(TranscriptRecord record) => _orchestrator.CopyRecordAsync(record);
    [RelayCommand] private void RequestDeleteHistory(TranscriptRecord record)
    {
        if (ReferenceEquals(PendingDeleteRecord, record)) { _ = ConfirmDeleteHistoryAsync(); return; }
        PendingDeleteRecord = record;
        _deleteConfirmationTimeout?.Cancel(); _deleteConfirmationTimeout?.Dispose();
        _deleteConfirmationTimeout = new();
        CancellationToken token = _deleteConfirmationTimeout.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(4), token); Dispatcher.UIThread.Post(() => PendingDeleteRecord = null); }
            catch (OperationCanceledException) { }
        });
    }
    [RelayCommand] private void CancelDeleteHistory() => PendingDeleteRecord = null;
    [RelayCommand]
    private async Task ConfirmDeleteHistoryAsync()
    {
        TranscriptRecord? record = PendingDeleteRecord;
        if (record is null) return;
        await _orchestrator.DeleteRecordAsync(record);
        PendingDeleteRecord = null;
    }
    [RelayCommand] private void RequestDeleteAll() => ShowDeleteAllConfirmation = true;
    [RelayCommand] private void CancelDeleteAll() => ShowDeleteAllConfirmation = false;
    [RelayCommand] private async Task ConfirmDeleteAllAsync() { await _orchestrator.DeleteAllHistoryAsync(); ShowDeleteAllConfirmation = false; }
    [RelayCommand] private void TestCue() => _cues.PlayAccepted(VoiceCommand.StartRecording, CueVolume);
    [RelayCommand] private void CancelDownload() => _downloadCancellation?.Cancel();

    [RelayCommand]
    private async Task DownloadVoiceModelAsync()
    {
        _lastDownloadKind = "voice";
        await RunDownloadAsync(async token =>
        {
            State.VoiceModelState = "Downloading";
            string path = await _downloads.InstallAsync(SelectedVoiceLanguage.ToInstallRequest(_paths.VoskModels), Progress(SelectedVoiceLanguage.DisplayName), token);
            State.VoiceModelState = "Activating";
            DownloadStatus = $"Activating {SelectedVoiceLanguage.DisplayName}";
            _orchestrator.InitializeVosk(path, SelectedVoiceLanguage);
            await _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { VoiceCommandLanguage = SelectedVoiceLanguage.Id }, token);
            DownloadStatus = $"{SelectedVoiceLanguage.DisplayName} is active";
            RefreshComputedStatus();
        });
    }

    [RelayCommand]
    private async Task DownloadDictationAssetsAsync()
    {
        _lastDownloadKind = "dictation";
        await RunDownloadAsync(async token =>
        {
            string modelId = SelectedDictationLanguage == "English" ? "parakeet-v2" : "parakeet-v3";
            ModelArtifact model = _modelCatalog.Get(modelId);
            State.DictationModelState = "Downloading";
            if (ShouldInstallOptionalGpuRuntime(_orchestrator.Readiness, ForceCpuOnly, _orchestrator.HasNvidiaGpu)) try
                {
                    ModelArtifact runtime = _modelCatalog.Get("sherpa-cuda-12");
                    await _downloads.InstallAsync(runtime.ToInstallRequest(_paths.RuntimeModels), Progress("NVIDIA GPU runtime"), token);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
                {
                    State.ProviderFallbackReason = "GPU runtime download failed; CPU remains available: " + ex.Message;
                }
            else if (_orchestrator.HasNvidiaGpu && !_orchestrator.Readiness.SetupCompletedOnce)
                State.ProviderFallbackReason = "First-time setup uses the built-in CPU path. Optional NVIDIA acceleration can be installed after setup.";
            else State.ProviderFallbackReason = ForceCpuOnly ? "CPU-only mode is enabled." : "No compatible NVIDIA GPU was detected; CPU will be used.";
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
        IsDownloading = true; DownloadFailed = false; DownloadPercent = 0; DownloadDetail = "";
        try { await operation(_downloadCancellation.Token); }
        catch (OperationCanceledException) { DownloadStatus = "Download canceled"; }
        catch (Exception ex)
        {
            DownloadFailed = true;
            bool voiceActivation = State.VoiceModelState == "Activating";
            bool dictationActivation = State.DictationModelState == "Initializing";
            DownloadStatus = (voiceActivation || dictationActivation ? "Activation failed: " : "Download failed: ") + ex.Message;
            if (_lastDownloadKind == "voice")
            {
                if (_orchestrator.ActiveVoiceCommandLanguageId is null) _orchestrator.MarkVoiceCommandsUnavailable();
                State.VoiceModelState = _orchestrator.ActiveVoiceCommandLanguageId is null
                    ? voiceActivation ? "Activation failed" : "Download failed"
                    : "Active";
            }
            else { _orchestrator.MarkDictationUnavailable(); State.DictationModelState = dictationActivation ? "Activation failed" : "Download failed"; }
        }
        finally { IsDownloading = false; _downloadCancellation.Dispose(); _downloadCancellation = null; RefreshComputedStatus(); }
    }

    [RelayCommand]
    private Task RetryDownloadAsync() => _lastDownloadKind == "voice" ? DownloadVoiceModelAsync() : DownloadDictationAssetsAsync();

    private Progress<ModelDownloadProgress> Progress(string name) => new(value => Dispatcher.UIThread.Post(() =>
    {
        DownloadPercent = value.Percentage ?? 0;
        DownloadStatus = $"{name} · {value.Stage}";
        DownloadDetail = value.TotalBytes is long total ? $"{FormatBytes(value.BytesDownloaded)} / {FormatBytes(total)}" : FormatBytes(value.BytesDownloaded);
    }));

    [RelayCommand]
    private async Task<bool> ApplySettingsAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(RecordingStartedShortcut)) _ = ShortcutGestureParser.ParseAction(RecordingStartedShortcut);
            if (!string.IsNullOrWhiteSpace(RecordingStoppedShortcut)) _ = ShortcutGestureParser.ParseAction(RecordingStoppedShortcut);
            HotkeyChangeResult hotkey = await _hotkey.ChangeAsync(HotkeyGesture);
            if (!hotkey.Success) { HotkeyValidation = hotkey.Error ?? "Shortcut could not be activated."; HotkeyGesture = hotkey.ActiveGesture; return false; }
            HotkeyGesture = hotkey.ActiveGesture; HotkeyValidation = "";
            DictationMode mode = SelectedDictationLanguage == "English" ? DictationMode.English : DictationMode.Automatic;
            var settings = _orchestrator.Settings with
            {
                VoiceCommandLanguage = SelectedVoiceLanguage.Id,
                DictationMode = mode,
                AudioDeviceId = SelectedAudioDevice?.Id,
                StartWithWindows = StartWithWindows,
                CopyOnStop = CopyOnStop,
                ShowFloatingPill = ShowFloatingPill,
                ForceCpuOnly = ForceCpuOnly,
                Theme = Theme,
                CueVolume = CueVolume,
                ToggleHotkey = HotkeyGesture,
                RecordingStartedShortcut = EmptyToNull(RecordingStartedShortcut),
                RecordingStoppedShortcut = EmptyToNull(RecordingStoppedShortcut),
                WordReplacementGroups = ReplacementGroups.Select(x => x.ToModel()).ToList()
            };
            await _orchestrator.UpdateSettingsAsync(settings);
            _startup.SetEnabled(StartWithWindows);
            ApplyTheme(Theme);
            CommandValidation = "Saved";
            RefreshComputedStatus();
            return true;
        }
        catch (Exception ex) { CommandValidation = ex.Message; return false; }
    }

    [RelayCommand]
    private async Task FinishOnboardingAsync()
    {
        if (!await ApplySettingsAsync()) return;
        if (!_orchestrator.Readiness.RequiredCapabilitiesReady)
        {
            DownloadStatus = "Install and initialize both models before finishing setup";
            return;
        }
        await _orchestrator.CompleteSetupAsync();
        await _globalHotkey.ApplyAsync(HotkeyGesture);
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

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        IsDownloading = true;
        DownloadStatus = "Downloading application update";
        try { await _updates.DownloadAndRestartAsync(new Progress<int>(value => DownloadPercent = value)); }
        finally { IsDownloading = false; }
    }
    [RelayCommand]
    private void ResetCommands()
    {
        _loading = true;
        foreach (CommandAliasEditorViewModel editor in BuiltInAliasEditors)
        {
            string key = VoiceCommandKeys.All[editor.Command];
            var values = new List<string> { SelectedVoiceLanguage.Commands[key] };
            if (SelectedVoiceLanguage.CommandAliases?.GetValueOrDefault(key) is { Count: > 0 } extras) values.AddRange(extras);
            editor.Aliases.Replace(values);
        }
        _loading = false;
        ScheduleCommandSave();
    }
    [RelayCommand] private void BeginHotkeyCapture() { ActiveShortcutCapture = ShortcutCaptureTarget.RecordingToggle; HotkeyValidation = "Press a shortcut…"; }
    [RelayCommand] private void BeginCustomShortcutCapture() { if (SelectedCustomCommand is not null) { ActiveShortcutCapture = ShortcutCaptureTarget.CustomCommand; CustomCommandValidation = "Press a shortcut…"; } }
    [RelayCommand] private void BeginRecordingStartedShortcutCapture() { ActiveShortcutCapture = ShortcutCaptureTarget.RecordingStarted; HotkeyValidation = "Press a shortcut…"; }
    [RelayCommand] private void BeginRecordingStoppedShortcutCapture() { ActiveShortcutCapture = ShortcutCaptureTarget.RecordingStopped; HotkeyValidation = "Press a shortcut…"; }
    [RelayCommand] private void ClearRecordingStartedShortcut() => RecordingStartedShortcut = "";
    [RelayCommand] private void ClearRecordingStoppedShortcut() => RecordingStoppedShortcut = "";
    [RelayCommand] private async Task ResetHotkeyAsync() { HotkeyGesture = "Ctrl+Space"; HotkeyChangeResult result = await _hotkey.ChangeAsync(HotkeyGesture); HotkeyValidation = result.Error ?? ""; }

    public async Task CaptureHotkeyAsync(string gesture)
    {
        HotkeyChangeResult result = await _hotkey.ChangeAsync(gesture);
        ActiveShortcutCapture = ShortcutCaptureTarget.None;
        HotkeyGesture = result.ActiveGesture;
        HotkeyValidation = result.Success ? "" : result.Error ?? "Shortcut could not be activated.";
        if (result.Success) await _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { ToggleHotkey = result.ActiveGesture });
    }

    public void CaptureCustomShortcut(string gesture)
    {
        ActiveShortcutCapture = ShortcutCaptureTarget.None;
        if (SelectedCustomCommand is null) return;
        try
        {
            SelectedCustomCommand.Shortcut = ShortcutGestureParser.ParseAction(gesture).ToString();
            CustomCommandValidation = "Shortcut captured. Save to apply.";
            OnPropertyChanged(nameof(SelectedCustomCommand));
        }
        catch (FormatException ex) { CustomCommandValidation = ex.Message; }
    }

    public void CaptureRecordingEventShortcut(string gesture)
    {
        try
        {
            string value = ShortcutGestureParser.ParseAction(gesture).ToString();
            if (IsCapturingRecordingStartedShortcut) RecordingStartedShortcut = value;
            if (IsCapturingRecordingStoppedShortcut) RecordingStoppedShortcut = value;
            HotkeyValidation = "Shortcut captured. Save to apply.";
        }
        catch (FormatException ex) { HotkeyValidation = ex.Message; }
        finally { ActiveShortcutCapture = ShortcutCaptureTarget.None; }
    }

    private void CancelShortcutCapture()
    {
        ActiveShortcutCapture = ShortcutCaptureTarget.None;
    }

    [RelayCommand]
    private void AddCustomCommand()
    {
        var command = new CustomVoiceCommand { VoiceCommandLanguageId = SelectedVoiceLanguage.Id, Name = "New command", CommandType = CustomCommandType.Program, Aliases = [""] };
        CustomCommands.Add(command); SelectedCustomCommand = command;
    }

    [RelayCommand]
    private async Task SaveCustomCommandAsync()
    {
        if (SelectedCustomCommand is null) return;
        try
        {
            SelectedCustomCommand.Aliases = CustomAliases.Values.ToList();
            SelectedCustomCommand.Phrase = SelectedCustomCommand.Aliases.FirstOrDefault() ?? "";
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

    [RelayCommand]
    private void AddWordReplacement()
    {
        ReplacementGroups.Add(new(new WordReplacementGroup { Matches = [""] }));
    }

    [RelayCommand]
    private async Task SaveWordReplacementsAsync()
    {
        try
        {
            List<WordReplacementGroup> groups = ReplacementGroups.Select(x => x.ToModel()).ToList();
            foreach (WordReplacementGroup group in groups) WordReplacementEngine.Validate(group);
            await _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { WordReplacementGroups = groups });
            CommandValidation = "Word replacements saved";
        }
        catch (Exception ex) { CommandValidation = ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteWordReplacementAsync(ReplacementGroupEditorViewModel replacement)
    {
        ReplacementGroups.Remove(replacement);
        await SaveWordReplacementsAsync();
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
                await _orchestrator.UpdateCommandAliasesAsync(SelectedVoiceLanguage.Id, CurrentAliases(), token);
                Dispatcher.UIThread.Post(() => { CommandValidation = "Saved"; OnPropertyChanged(nameof(MainCommandText)); });
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
            catch (Exception ex) { _orchestrator.MarkVoiceCommandsUnavailable(); DownloadStatus = "Voice model initialization failed: " + ex.Message; }
        }
        else _orchestrator.MarkVoiceCommandsUnavailable();
        string modelId = SelectedDictationLanguage == "English" ? "parakeet-v2" : "parakeet-v3";
        ModelArtifact model = _modelCatalog.Get(modelId); ModelArtifact vad = _modelCatalog.Get("silero-vad");
        string modelPath = Path.Combine(_paths.DictationModels, model.ExpectedDirectory);
        string vadPath = Path.Combine(_paths.DictationModels, vad.ExpectedDirectory);
        if (IsInstalled(modelPath, model) && IsInstalled(vadPath, vad))
        {
            try { await _orchestrator.InitializeParakeetAsync(modelPath, model, vadPath); }
            catch (Exception ex) { _orchestrator.MarkDictationUnavailable(); DownloadStatus = "Dictation initialization failed: " + ex.Message; }
        }
        else _orchestrator.MarkDictationUnavailable();
    }

    private static bool IsInstalled(string directory, ModelArtifact artifact) => artifact.RequiredFiles.All(file => File.Exists(Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar))));
    private static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch { AppTheme.Light => ThemeVariant.Light, AppTheme.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }
    private void RefreshComputedStatus()
    {
        OnPropertyChanged(nameof(VoiceLanguageStatus)); OnPropertyChanged(nameof(CurrentListenerStatus)); OnPropertyChanged(nameof(DiagnosticsSummary)); OnPropertyChanged(nameof(MainCommandText));
        OnPropertyChanged(nameof(ParakeetV2Status)); OnPropertyChanged(nameof(ParakeetV3Status)); OnPropertyChanged(nameof(AccelerationStatus));
        OnPropertyChanged(nameof(ShowNvidiaMark));
        OnPropertyChanged(nameof(CanContinueOnboarding));
    }
    private static string FormatBytes(long bytes) => bytes switch { >= 1_073_741_824 => $"{bytes / 1_073_741_824d:F1} GB", >= 1_048_576 => $"{bytes / 1_048_576d:F1} MB", >= 1024 => $"{bytes / 1024d:F1} KB", _ => $"{bytes} B" };
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private bool IsInstalledArtifact(string id)
    {
        ModelArtifact artifact = _modelCatalog.Get(id);
        return IsInstalled(Path.Combine(_paths.DictationModels, artifact.ExpectedDirectory), artifact);
    }
    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture) : elapsed.ToString(@"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    internal static bool ShouldInstallOptionalGpuRuntime(ApplicationReadiness readiness, bool forceCpuOnly, bool hasNvidiaGpu) =>
        readiness.SetupCompletedOnce && !forceCpuOnly && hasNvidiaGpu;
    private string Animate(string value)
    {
        string label = value.TrimEnd('.', '…');
        return label.StartsWith("Recording", StringComparison.Ordinal) || label.StartsWith("Pasting", StringComparison.Ordinal) || label.StartsWith("Preparing", StringComparison.Ordinal)
            ? label + new string('.', _animationPhase) : value;
    }
    private void OnSpectrumFrame(object? sender, IReadOnlyList<double> frame) => Dispatcher.UIThread.Post(() => SpectrumBars = frame);

    public bool ShouldShowCloseToTrayNotice => !_orchestrator.Settings.CloseToTrayNoticeShown;
    public Task MarkCloseToTrayNoticeShownAsync() => _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { CloseToTrayNoticeShown = true });

    public void Dispose()
    {
        _elapsedTimer.Stop();
        _spectrum.FrameReady -= OnSpectrumFrame;
        _downloadCancellation?.Cancel(); _downloadCancellation?.Dispose(); _downloadCancellation = null;
        _commandSaveDebounce?.Cancel(); _commandSaveDebounce?.Dispose(); _commandSaveDebounce = null;
        _deleteConfirmationTimeout?.Cancel(); _deleteConfirmationTimeout?.Dispose(); _deleteConfirmationTimeout = null;
        GC.SuppressFinalize(this);
    }
}
