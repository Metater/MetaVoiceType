using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Core.State;
using MetaVoiceType.Models;
using MetaVoiceType.Sessions;
using MetaVoiceType.Storage;
using MetaVoiceType.VoiceCommands;

namespace MetaVoiceType.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ApplicationOrchestrator _orchestrator;
    private readonly IModelDownloadService _downloads;
    private readonly IStartupService _startup;
    private readonly IGlobalHotkeyService _hotkey;
    private readonly IUpdateService _updates;
    private readonly IAudioCaptureService _audio;
    private readonly AppPaths _paths;
    private readonly VoiceCommandCatalog _voiceCatalog = VoiceCommandCatalog.LoadBundled();
    private readonly ModelCatalog _modelCatalog = ModelCatalog.LoadBundled();

    public MainViewModel(ApplicationOrchestrator orchestrator, IModelDownloadService downloads,
        IStartupService startup, IGlobalHotkeyService hotkey, IUpdateService updates, IAudioCaptureService audio, AppPaths paths)
    {
        _orchestrator = orchestrator; _downloads = downloads; _startup = startup; _hotkey = hotkey; _updates = updates; _audio = audio; _paths = paths;
        State = orchestrator.State;
        Languages = new(_voiceCatalog.Languages);
        AudioDevices = new(_audio.EnumerateDevices());
        SelectedAudioDevice = AudioDevices.FirstOrDefault(x => x.IsDefault) ?? AudioDevices.FirstOrDefault();
        SelectedVoiceLanguage = _voiceCatalog.Get(_voiceCatalog.DefaultLanguage);
        _hotkey.ToggleRecording += (_, _) => ToggleRecording();
        _ = InitializeAsync();
    }

    public MetaVoiceTypeState State { get; }
    public ObservableCollection<VoiceCommandLanguage> Languages { get; }
    public ObservableCollection<AudioDevice> AudioDevices { get; }
    public IReadOnlyList<string> DictationLanguages => ["auto", .. _modelCatalog.Nemotron.Languages.TranscriptionReady,
        .. _modelCatalog.Nemotron.Languages.BroadCoverage, .. _modelCatalog.Nemotron.Languages.AdaptationReady];
    public IReadOnlyList<AppTheme> Themes { get; } = [AppTheme.Dark, AppTheme.Light, AppTheme.System];

    [ObservableProperty] public partial bool ShowOnboarding { get; set; } = true;
    [ObservableProperty] public partial bool ShowSettings { get; set; }
    [ObservableProperty] public partial int OnboardingStep { get; set; } = 1;
    [ObservableProperty] public partial VoiceCommandLanguage SelectedVoiceLanguage { get; set; }
    [ObservableProperty] public partial AudioDevice? SelectedAudioDevice { get; set; }
    [ObservableProperty] public partial string SelectedDictationLanguage { get; set; } = "auto";
    [ObservableProperty] public partial bool StartWithWindows { get; set; }
    [ObservableProperty] public partial bool CopyOnStop { get; set; } = true;
    [ObservableProperty] public partial bool ShowFloatingPill { get; set; } = true;
    [ObservableProperty] public partial AppTheme Theme { get; set; } = AppTheme.Dark;
    [ObservableProperty] public partial double CueVolume { get; set; } = 0.6;
    [ObservableProperty] public partial double DownloadPercent { get; set; }
    [ObservableProperty] public partial string DownloadStatus { get; set; } = "";
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

    public bool IsWelcomeStep => OnboardingStep == 1;
    public bool IsVoiceStep => OnboardingStep == 2;
    public bool IsNemotronStep => OnboardingStep == 3;
    public bool IsMicrophoneStep => OnboardingStep == 4;
    public bool IsStartupStep => OnboardingStep == 5;
    public bool IsReadyStep => OnboardingStep == 6;

    private async Task InitializeAsync()
    {
        await _orchestrator.InitializeAsync();
        AppSettings settings = _orchestrator.Settings;
        SelectedVoiceLanguage = _voiceCatalog.Get(settings.VoiceCommandLanguage);
        SelectedAudioDevice = AudioDevices.FirstOrDefault(x => x.Id == settings.AudioDeviceId) ?? AudioDevices.FirstOrDefault(x => x.IsDefault) ?? AudioDevices.FirstOrDefault();
        SelectedDictationLanguage = settings.DictationLanguage; StartWithWindows = settings.StartWithWindows; CopyOnStop = settings.CopyOnStop;
        ShowFloatingPill = settings.ShowFloatingPill; Theme = settings.Theme; CueVolume = settings.CueVolume;
        ApplyTheme(Theme);
        ShowOnboarding = !settings.OnboardingComplete;
        LoadPhrases();
        TryInitializeInstalledModels();
        await _hotkey.StartAsync();
        if (settings.CheckForUpdates) await CheckForUpdatesAsync();
    }

    partial void OnOnboardingStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsWelcomeStep)); OnPropertyChanged(nameof(IsVoiceStep)); OnPropertyChanged(nameof(IsNemotronStep));
        OnPropertyChanged(nameof(IsMicrophoneStep)); OnPropertyChanged(nameof(IsStartupStep)); OnPropertyChanged(nameof(IsReadyStep));
    }
    partial void OnSelectedVoiceLanguageChanged(VoiceCommandLanguage value) { if (value is not null) LoadPhrases(); }

    private void LoadPhrases()
    {
        if (SelectedVoiceLanguage is null) return;
        IReadOnlyDictionary<VoiceCommand, string> values = _orchestrator.ResolvePhrases(SelectedVoiceLanguage);
        StartRecordingPhrase = values[VoiceCommand.StartRecording]; StopRecordingPhrase = values[VoiceCommand.StopRecording]; PasteHerePhrase = values[VoiceCommand.PasteHere];
        CancelRecordingPhrase = values[VoiceCommand.CancelRecording]; CancelPastePhrase = values[VoiceCommand.CancelPaste]; CopyPhrase = values[VoiceCommand.CopyRecordingToClipboard];
    }

    private Dictionary<VoiceCommand, string> CurrentPhrases() => new()
    {
        [VoiceCommand.StartRecording] = StartRecordingPhrase, [VoiceCommand.StopRecording] = StopRecordingPhrase, [VoiceCommand.PasteHere] = PasteHerePhrase,
        [VoiceCommand.CancelRecording] = CancelRecordingPhrase, [VoiceCommand.CancelPaste] = CancelPastePhrase, [VoiceCommand.CopyRecordingToClipboard] = CopyPhrase
    };

    [RelayCommand] private void NextOnboarding() { if (OnboardingStep < 6) OnboardingStep++; }
    [RelayCommand] private void PreviousOnboarding() { if (OnboardingStep > 1) OnboardingStep--; }
    [RelayCommand] private void ToggleSettings() => ShowSettings = !ShowSettings;
    [RelayCommand] private void ToggleRecording() { if (State.IsRecording) _orchestrator.StopRecording(); else _orchestrator.StartRecording(); }
    [RelayCommand] private void Paste() => _orchestrator.PasteHere();
    [RelayCommand] private void CancelRecording() => _orchestrator.StopRecording(canceled: true);
    [RelayCommand] private Task CopyAsync() => _orchestrator.CopyCurrentAsync();

    [RelayCommand]
    private async Task DownloadVoiceModelAsync()
    {
        IsDownloading = true;
        try
        {
            var request = new ModelInstallRequest(SelectedVoiceLanguage.ArchiveUrl, "zip", SelectedVoiceLanguage.ModelName, _paths.VoskModels, null,
                SelectedVoiceLanguage.SizeBytes, ["am/final.mdl", "conf/mfcc.conf"]);
            string path = await _downloads.InstallAsync(request, Progress());
            _orchestrator.InitializeVosk(path, SelectedVoiceLanguage);
            DownloadStatus = "Voice command model ready";
        }
        catch (Exception ex) { DownloadStatus = "Download failed: " + ex.Message; }
        finally { IsDownloading = false; }
    }

    [RelayCommand]
    private async Task DownloadNemotronAsync()
    {
        IsDownloading = true;
        try
        {
            DictationModel model = _modelCatalog.Nemotron;
            var request = new ModelInstallRequest(model.ArchiveUrl, model.ArchiveType, model.ExtractedDirectory, _paths.NemotronModels,
                model.ArchiveSha256, model.EstimatedDownloadBytes, model.RequiredFiles);
            string path = await _downloads.InstallAsync(request, Progress());
            _orchestrator.InitializeNemotron(path);
            DownloadStatus = "Nemotron ready";
        }
        catch (Exception ex) { DownloadStatus = "Download failed: " + ex.Message; }
        finally { IsDownloading = false; }
    }

    private Progress<ModelDownloadProgress> Progress() => new(p => { DownloadStatus = p.Stage; DownloadPercent = p.Percentage ?? 0; });

    [RelayCommand]
    private async Task ApplySettingsAsync()
    {
        try
        {
            await _orchestrator.UpdateCommandPhrasesAsync(SelectedVoiceLanguage.Id, CurrentPhrases());
            CommandValidation = "Commands updated";
        }
        catch (InvalidDataException ex) { CommandValidation = ex.Message; return; }
        AppSettings next = _orchestrator.Settings with { VoiceCommandLanguage = SelectedVoiceLanguage.Id, DictationLanguage = SelectedDictationLanguage,
            AudioDeviceId = SelectedAudioDevice?.Id, StartWithWindows = StartWithWindows, CopyOnStop = CopyOnStop, ShowFloatingPill = ShowFloatingPill, Theme = Theme, CueVolume = CueVolume };
        await _orchestrator.UpdateSettingsAsync(next); _startup.SetEnabled(StartWithWindows); ApplyTheme(Theme);
        string vosk = Path.Combine(_paths.VoskModels, SelectedVoiceLanguage.ModelName);
        if (Directory.Exists(vosk)) _orchestrator.InitializeVosk(vosk, SelectedVoiceLanguage);
        else DownloadStatus = "Download the selected voice-command model to activate it";
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            if (!_updates.IsInstalled) { UpdateStatus = "Updates are available after installing MetaVoiceType"; UpdateAvailable = false; return; }
            UpdateStatus = "Checking…";
            string? version = await _updates.CheckAsync();
            UpdateAvailable = version is not null;
            UpdateStatus = version is null ? "MetaVoiceType is up to date" : $"Version {version} is available";
        }
        catch (Exception ex) { UpdateAvailable = false; UpdateStatus = "Update check failed: " + ex.Message; }
    }

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        try
        {
            UpdateStatus = "Downloading update…";
            await _updates.DownloadAndRestartAsync(new Progress<int>(value => UpdateStatus = $"Downloading update… {value}%"));
        }
        catch (Exception ex) { UpdateStatus = "Update failed: " + ex.Message; }
    }

    private static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }

    [RelayCommand] private void ResetCommands() { StartRecordingPhrase = SelectedVoiceLanguage.Commands["startRecording"]; StopRecordingPhrase = SelectedVoiceLanguage.Commands["stopRecording"]; PasteHerePhrase = SelectedVoiceLanguage.Commands["pasteHere"]; CancelRecordingPhrase = SelectedVoiceLanguage.Commands["cancelRecording"]; CancelPastePhrase = SelectedVoiceLanguage.Commands["cancelPaste"]; CopyPhrase = SelectedVoiceLanguage.Commands["copyRecordingToClipboard"]; }

    public bool ShouldShowCloseToTrayNotice => !_orchestrator.Settings.CloseToTrayNoticeShown;
    public Task MarkCloseToTrayNoticeShownAsync() => _orchestrator.UpdateSettingsAsync(_orchestrator.Settings with { CloseToTrayNoticeShown = true });

    [RelayCommand]
    private async Task FinishOnboardingAsync()
    {
        var settings = _orchestrator.Settings with
        {
            OnboardingComplete = true,
            VoiceCommandLanguage = SelectedVoiceLanguage.Id,
            DictationLanguage = SelectedDictationLanguage,
            AudioDeviceId = SelectedAudioDevice?.Id,
            StartWithWindows = StartWithWindows,
            CopyOnStop = CopyOnStop,
            ShowFloatingPill = ShowFloatingPill,
            Theme = Theme,
            CueVolume = CueVolume
        };
        await _orchestrator.UpdateSettingsAsync(settings); _startup.SetEnabled(StartWithWindows); ShowOnboarding = false;
    }

    private void TryInitializeInstalledModels()
    {
        string vosk = Path.Combine(_paths.VoskModels, SelectedVoiceLanguage.ModelName);
        if (Directory.Exists(vosk)) _orchestrator.InitializeVosk(vosk, SelectedVoiceLanguage);
        string nemotron = Path.Combine(_paths.NemotronModels, _modelCatalog.Nemotron.ExtractedDirectory);
        if (Directory.Exists(nemotron) && _modelCatalog.Nemotron.RequiredFiles.All(x => File.Exists(Path.Combine(nemotron, x)))) _orchestrator.InitializeNemotron(nemotron);
    }
}
