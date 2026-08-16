using CommunityToolkit.Mvvm.ComponentModel;
using MetaVoiceType.Core.Interfaces;

namespace MetaVoiceType.Core.State;

public enum ApplicationReadinessState { SetupIncomplete, Initializing, Ready, Degraded }
public enum ApplicationAction { ManualRecording, GlobalRecordingShortcut, VoiceCommand, CustomAutomation, RecordingEventShortcut, PasteOrCopy }

public sealed partial class ApplicationReadiness : ObservableObject
{
    private bool _initializationComplete;
    [ObservableProperty] public partial ApplicationReadinessState State { get; private set; } = ApplicationReadinessState.SetupIncomplete;
    [ObservableProperty] public partial bool SetupCompletedOnce { get; private set; }
    [ObservableProperty] public partial bool MicrophoneReady { get; private set; }
    [ObservableProperty] public partial bool DictationReady { get; private set; }
    [ObservableProperty] public partial bool VoiceCommandsReady { get; private set; }

    public bool CanRecord => SetupCompletedOnce && MicrophoneReady && DictationReady && State is ApplicationReadinessState.Ready or ApplicationReadinessState.Degraded;
    public bool CanUseGlobalRecordingShortcut => CanRecord;
    public bool CanUseVoiceCommands => State == ApplicationReadinessState.Ready && VoiceCommandsReady;
    public bool CanUseCustomAutomations => State == ApplicationReadinessState.Ready;
    public bool CanUseRecordingEventShortcuts => State == ApplicationReadinessState.Ready;
    public bool CanPaste => SetupCompletedOnce && State is ApplicationReadinessState.Ready or ApplicationReadinessState.Degraded;
    public bool RequiredCapabilitiesReady => MicrophoneReady && DictationReady && VoiceCommandsReady;

    public void BeginInitialization(bool setupCompletedOnce)
    {
        SetupCompletedOnce = setupCompletedOnce;
        MicrophoneReady = false;
        DictationReady = false;
        VoiceCommandsReady = false;
        _initializationComplete = false;
        State = ApplicationReadinessState.Initializing;
        NotifyCapabilities();
    }

    public void SetMicrophoneReady(bool ready) { MicrophoneReady = ready; Reevaluate(); }
    public void SetDictationReady(bool ready) { DictationReady = ready; Reevaluate(); }
    public void SetVoiceCommandsReady(bool ready) { VoiceCommandsReady = ready; Reevaluate(); }

    public void CompleteInitialization() { _initializationComplete = true; Reevaluate(); }

    public void MarkSetupCompleted()
    {
        if (!MicrophoneReady || !DictationReady || !VoiceCommandsReady)
            throw new InvalidOperationException("Setup cannot complete until the microphone, dictation engine, and voice-command recognizer are ready.");
        SetupCompletedOnce = true;
        _initializationComplete = true;
        State = ApplicationReadinessState.Ready;
        NotifyCapabilities();
    }

    private void Reevaluate()
    {
        if (!_initializationComplete) { NotifyCapabilities(); return; }
        State = MicrophoneReady && DictationReady && VoiceCommandsReady && SetupCompletedOnce
            ? ApplicationReadinessState.Ready
            : SetupCompletedOnce ? ApplicationReadinessState.Degraded : ApplicationReadinessState.SetupIncomplete;
        NotifyCapabilities();
    }

    private void NotifyCapabilities()
    {
        OnPropertyChanged(nameof(CanRecord));
        OnPropertyChanged(nameof(CanUseGlobalRecordingShortcut));
        OnPropertyChanged(nameof(CanUseVoiceCommands));
        OnPropertyChanged(nameof(CanUseCustomAutomations));
        OnPropertyChanged(nameof(CanUseRecordingEventShortcuts));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(RequiredCapabilitiesReady));
    }
}

public sealed class ApplicationActionCoordinator(ApplicationReadiness readiness)
{
    public ApplicationReadiness Readiness => readiness;

    public bool IsAllowed(ApplicationAction action) => action switch
    {
        ApplicationAction.ManualRecording => readiness.CanRecord,
        ApplicationAction.GlobalRecordingShortcut => readiness.CanUseGlobalRecordingShortcut,
        ApplicationAction.VoiceCommand => readiness.CanUseVoiceCommands,
        ApplicationAction.CustomAutomation => readiness.CanUseCustomAutomations,
        ApplicationAction.RecordingEventShortcut => readiness.CanUseRecordingEventShortcuts,
        ApplicationAction.PasteOrCopy => readiness.CanPaste,
        _ => false
    };
}

public sealed class GlobalHotkeyRegistration(IGlobalHotkeyService hotkey, ApplicationActionCoordinator actions)
{
    public Task ApplyAsync(string gesture, CancellationToken cancellationToken = default) =>
        actions.IsAllowed(ApplicationAction.GlobalRecordingShortcut)
            ? hotkey.StartAsync(gesture, cancellationToken)
            : hotkey.StopAsync(cancellationToken);
}
