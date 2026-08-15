using CommunityToolkit.Mvvm.ComponentModel;
using MetaVoiceType.Core.Models;

namespace MetaVoiceType.Core.State;

public partial class MetaVoiceTypeState : ObservableObject
{
    [ObservableProperty] public partial bool CommandListenerActive { get; set; }
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial bool PastePending { get; set; }
    [ObservableProperty] public partial string LiveTranscript { get; set; } = "";
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Getting ready…";
    [ObservableProperty] public partial double AudioLevel { get; set; }
    [ObservableProperty] public partial DateTimeOffset? RecordingStartedAt { get; set; }
    [ObservableProperty] public partial string Acceleration { get; set; } = "Not installed";
    public System.Collections.ObjectModel.ObservableCollection<TranscriptRecord> History { get; } = [];
}
