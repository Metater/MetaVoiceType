namespace MetaVoiceType.Core.Models;

public enum VoiceCommand
{
    StartRecording,
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
        [VoiceCommand.StopRecording] = "stopRecording",
        [VoiceCommand.PasteHere] = "pasteHere",
        [VoiceCommand.CancelRecording] = "cancelRecording",
        [VoiceCommand.CancelPaste] = "cancelPaste",
        [VoiceCommand.CopyRecordingToClipboard] = "copyRecordingToClipboard"
    };
}
