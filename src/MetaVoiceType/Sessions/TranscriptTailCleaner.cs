namespace MetaVoiceType.Sessions;

public static class TranscriptTailCleaner
{
    public static string RemoveAcceptedCommandTail(string transcript, string? acceptedPhrase)
    {
        if (string.IsNullOrWhiteSpace(acceptedPhrase)) return transcript.Trim();
        string phrase = VoiceCommands.CommandPhraseValidator.Normalize(acceptedPhrase);
        string normalized = VoiceCommands.CommandPhraseValidator.Normalize(transcript);
        if (!normalized.EndsWith(phrase, StringComparison.OrdinalIgnoreCase)) return transcript.Trim();
        int index = transcript.LastIndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return transcript.Trim();
        return transcript[..index].TrimEnd(' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?');
    }
}
