using CommunityToolkit.Mvvm.ComponentModel;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Sessions;

namespace MetaVoiceType.Core.State;

public partial class MetaVoiceTypeState : ObservableObject
{
    [ObservableProperty] public partial bool CommandListenerActive { get; set; }
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial PasteRequestState PasteState { get; set; }
    public bool IsPasteActive => PasteState is PasteRequestState.Queued or PasteRequestState.Preparing or PasteRequestState.Pasting;
    [ObservableProperty] public partial string LiveTranscript { get; set; } = "";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Getting ready…";
    [ObservableProperty] public partial double AudioLevel { get; set; }
    [ObservableProperty] public partial DateTimeOffset? RecordingStartedAt { get; set; }
    [ObservableProperty] public partial string Acceleration { get; set; } = "Not installed";
    [ObservableProperty] public partial string EngineLabel { get; set; } = "Dictation model not installed";
    [ObservableProperty] public partial string? ProviderFallbackReason { get; set; }
    [ObservableProperty] public partial string SelectedVoiceLanguageId { get; set; } = "en-us";
    [ObservableProperty] public partial string? ActiveVoiceLanguageId { get; set; }
    [ObservableProperty] public partial string VoiceModelState { get; set; } = "Not installed";
    [ObservableProperty] public partial string DictationModelState { get; set; } = "Not installed";
    [ObservableProperty] public partial string TransientFeedback { get; set; } = "";
    public System.Collections.ObjectModel.ObservableCollection<TranscriptRecord> History { get; } = [];

    partial void OnPasteStateChanged(PasteRequestState value) => OnPropertyChanged(nameof(IsPasteActive));
}
