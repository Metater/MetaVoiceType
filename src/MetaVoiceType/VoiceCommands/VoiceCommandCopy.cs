using MetaVoiceType.Core.Models;

namespace MetaVoiceType.VoiceCommands;

public static class VoiceCommandCopy
{
    public static string ForRecordingState(bool isRecording, IReadOnlyDictionary<VoiceCommand, string> activePhrases) =>
        $"Say \"{activePhrases[isRecording ? VoiceCommand.StopRecording : VoiceCommand.StartRecording]}\"";

    public static string ForRecordingState(bool isRecording, IReadOnlyDictionary<VoiceCommand, IReadOnlyList<string>> activeAliases)
    {
        IReadOnlyList<string> aliases = activeAliases[isRecording ? VoiceCommand.StopRecording : VoiceCommand.StartRecording];
        return aliases.Count > 1 ? $"Say \"{aliases[0]}\" or \"{aliases[1]}\"" : $"Say \"{aliases[0]}\"";
    }
}
