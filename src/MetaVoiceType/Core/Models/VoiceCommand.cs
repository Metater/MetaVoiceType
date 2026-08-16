namespace MetaVoiceType.Core.Models;

public enum VoiceCommand
{
    StartRecording,
    ContinueRecording,
    StopRecording,
    PasteHere,
    CancelRecording,
    CancelPaste,
    CopyRecordingToClipboard
}

public static class VoiceCommandKeys
{
    public static readonly IReadOnlyDictionary<VoiceCommand, string> All = new Dictionary<VoiceCommand, string>
    {
        [VoiceCommand.StartRecording] = "startRecording",
        [VoiceCommand.ContinueRecording] = "continueRecording",
        [VoiceCommand.StopRecording] = "stopRecording",
        [VoiceCommand.PasteHere] = "pasteHere",
        [VoiceCommand.CancelRecording] = "cancelRecording",
        [VoiceCommand.CancelPaste] = "cancelPaste",
        [VoiceCommand.CopyRecordingToClipboard] = "copyRecordingToClipboard"
    };
}

public sealed record VoiceCommandDefinition(string Id, string Phrase, VoiceCommand? BuiltInCommand = null)
{
    public static VoiceCommandDefinition BuiltIn(VoiceCommand command, string phrase) => new(VoiceCommandKeys.All[command], phrase, command);
}
