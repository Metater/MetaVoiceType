namespace MetaVoiceType.Sessions;

public static class TranscriptTailCleaner
{
    public static string RemoveAcceptedCommandTail(string transcript, string? acceptedPhrase)
        => RemoveAcceptedCommandBoundary(transcript, acceptedPhrase);

    public static string RemoveAcceptedCommandBoundary(string transcript, string? acceptedPhrase)
    {
        if (string.IsNullOrWhiteSpace(acceptedPhrase)) return transcript.Trim();
        string phrase = VoiceCommands.CommandPhraseValidator.Normalize(acceptedPhrase);
        string normalized = VoiceCommands.CommandPhraseValidator.Normalize(transcript);
        if (phrase.Length == 0 || normalized.Length == 0) return transcript.Trim();

        string[] commandWords = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] transcriptWords = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int matchLength = Math.Min(commandWords.Length, transcriptWords.Length);
        while (matchLength > 0)
        {
            string candidate = string.Join(' ', commandWords.Take(matchLength));
            if (normalized.StartsWith(candidate + " ", StringComparison.OrdinalIgnoreCase) || normalized.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return RemoveWords(transcript, matchLength, fromStart: true);
            candidate = string.Join(' ', commandWords.Skip(commandWords.Length - matchLength));
            if (normalized.EndsWith(" " + candidate, StringComparison.OrdinalIgnoreCase) || normalized.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return RemoveWords(transcript, matchLength, fromStart: false);
            matchLength--;
        }
        return transcript.Trim();
    }

    private static string RemoveWords(string transcript, int count, bool fromStart)
    {
        char[] separators = [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?'];
        string[] words = transcript.Trim().Split(separators, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> remaining = fromStart ? words.Skip(count) : words.Take(Math.Max(0, words.Length - count));
        return string.Join(' ', remaining).Trim();
    }
}
