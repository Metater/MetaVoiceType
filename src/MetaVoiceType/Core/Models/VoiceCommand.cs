namespace MetaVoiceType.Core.Models;

public enum VoiceCommand
{
    StartRecording,
    ContinueRecording,
    StopRecording,
    PasteRecording,
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
        [VoiceCommand.PasteRecording] = "pasteRecording",
        [VoiceCommand.CancelRecording] = "cancelRecording",
        [VoiceCommand.CancelPaste] = "cancelPaste",
        [VoiceCommand.CopyRecordingToClipboard] = "copyRecordingToClipboard"
    };

    public const string LegacyPasteHere = "pasteHere";

    public static string Current(string id) => id.Equals(LegacyPasteHere, StringComparison.OrdinalIgnoreCase)
        ? All[VoiceCommand.PasteRecording]
        : id;
}

public sealed record VoiceCommandDefinition(string Id, IReadOnlyList<string> Aliases, VoiceCommand? BuiltInCommand = null)
{
    public static VoiceCommandDefinition BuiltIn(VoiceCommand command, params string[] aliases) => new(VoiceCommandKeys.All[command], aliases, command);
}
