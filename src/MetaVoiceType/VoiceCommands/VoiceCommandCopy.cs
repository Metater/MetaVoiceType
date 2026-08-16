using MetaVoiceType.Core.Models;

namespace MetaVoiceType.VoiceCommands;

public static class VoiceCommandCopy
{
    public static string ForRecordingState(bool isRecording, IReadOnlyDictionary<VoiceCommand, string> activePhrases) =>
        $"Say \"{activePhrases[isRecording ? VoiceCommand.StopRecording : VoiceCommand.StartRecording]}\"";
}
