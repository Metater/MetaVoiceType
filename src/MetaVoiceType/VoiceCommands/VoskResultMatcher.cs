using System.Text.Json;
using MetaVoiceType.Audio;
using MetaVoiceType.Core.Models;

namespace MetaVoiceType.VoiceCommands;

public sealed record VoiceCommandMatch(
    string CommandId,
    VoiceCommand? Command,
    string Phrase,
    string RecognizedText,
    int Position,
    double? Confidence,
    long? AudioStartSample,
    long? AudioEndSample,
    DateTimeOffset AcceptedAt)
{
    public TimeSpan? AudioStartTime => AudioStartSample is long value ? TimeSpan.FromSeconds(value / (double)AudioFrame.SampleRate) : null;
    public TimeSpan? AudioEndTime => AudioEndSample is long value ? TimeSpan.FromSeconds(value / (double)AudioFrame.SampleRate) : null;
}

public static class VoskResultMatcher
{
    private sealed record Word(string Text, double Start, double End);
    private sealed record Alternative(string Text, double? Confidence, IReadOnlyList<Word> Words);

    public static IReadOnlyList<VoiceCommandMatch> Match(string resultJson, IReadOnlyDictionary<VoiceCommand, string> configured, long recognizerBaseSample = 0) =>
        Match(resultJson, configured.Select(x => VoiceCommandDefinition.BuiltIn(x.Key, x.Value)).ToArray(), recognizerBaseSample);

    public static IReadOnlyList<VoiceCommandMatch> Match(string resultJson, IReadOnlyList<VoiceCommandDefinition> configured, long recognizerBaseSample = 0)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return [];
        using JsonDocument document = JsonDocument.Parse(resultJson);
        foreach (Alternative alternative in ParseAlternatives(document.RootElement))
        {
            string text = CommandPhraseValidator.Normalize(alternative.Text);
            if (text.Length == 0 || text == "[unk]") continue;
            var candidates = new List<(VoiceCommandDefinition Definition, int Position)>();
            foreach (VoiceCommandDefinition definition in configured)
            {
                string phrase = CommandPhraseValidator.Normalize(definition.Phrase);
                int offset = 0;
                while ((offset = text.IndexOf(phrase, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    bool left = offset == 0 || char.IsWhiteSpace(text[offset - 1]);
                    int end = offset + phrase.Length;
                    bool right = end == text.Length || char.IsWhiteSpace(text[end]);
                    if (left && right) candidates.Add((definition, offset));
                    offset = Math.Max(end, offset + 1);
                }
            }

            if (candidates.Count == 0) continue;
            DateTimeOffset acceptedAt = DateTimeOffset.UtcNow;
            return candidates
                .Where(candidate => !candidates.Any(other => other.Position == candidate.Position && other.Definition.Phrase.Length > candidate.Definition.Phrase.Length
                    && CommandPhraseValidator.Normalize(other.Definition.Phrase).Contains(CommandPhraseValidator.Normalize(candidate.Definition.Phrase), StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Position).ThenByDescending(x => x.Definition.Phrase.Length)
                .Select(candidate => CreateMatch(candidate.Definition, candidate.Position, alternative, recognizerBaseSample, acceptedAt)).ToArray();
        }
        return [];
    }

    private static VoiceCommandMatch CreateMatch(VoiceCommandDefinition definition, int position, Alternative alternative, long baseSample, DateTimeOffset acceptedAt)
    {
        string phrase = CommandPhraseValidator.Normalize(definition.Phrase);
        string normalizedWords = string.Join(' ', alternative.Words.Select(x => CommandPhraseValidator.Normalize(x.Text)));
        int wordPhrasePosition = normalizedWords.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        long? startSample = null;
        long? endSample = null;
        string recognized = phrase;
        if (wordPhrasePosition >= 0 && alternative.Words.Count > 0)
        {
            int cursor = 0;
            int first = -1;
            int last = -1;
            for (int i = 0; i < alternative.Words.Count; i++)
            {
                string word = CommandPhraseValidator.Normalize(alternative.Words[i].Text);
                int start = cursor;
                int end = start + word.Length;
                if (end > wordPhrasePosition && start < wordPhrasePosition + phrase.Length)
                {
                    if (first < 0) first = i;
                    last = i;
                }
                cursor = end + 1;
            }
            if (first >= 0 && last >= first)
            {
                startSample = baseSample + (long)Math.Round(alternative.Words[first].Start * AudioFrame.SampleRate, MidpointRounding.AwayFromZero);
                endSample = baseSample + (long)Math.Round(alternative.Words[last].End * AudioFrame.SampleRate, MidpointRounding.AwayFromZero);
                recognized = string.Join(' ', alternative.Words.Skip(first).Take(last - first + 1).Select(x => x.Text));
            }
        }
        return new(definition.Id, definition.BuiltInCommand, phrase, recognized, position, alternative.Confidence, startSample, endSample, acceptedAt);
    }

    private static IEnumerable<Alternative> ParseAlternatives(JsonElement root)
    {
        if (root.TryGetProperty("alternatives", out JsonElement values))
        {
            foreach (JsonElement value in values.EnumerateArray()) yield return ParseAlternative(value);
        }
        else yield return ParseAlternative(root);
    }

    private static Alternative ParseAlternative(JsonElement value)
    {
        string text = value.TryGetProperty("text", out JsonElement textValue) ? textValue.GetString() ?? "" : "";
        double? confidence = value.TryGetProperty("confidence", out JsonElement confidenceValue) && confidenceValue.TryGetDouble(out double parsed) ? parsed : null;
        var words = new List<Word>();
        if (value.TryGetProperty("result", out JsonElement result))
        {
            foreach (JsonElement item in result.EnumerateArray())
            {
                if (!item.TryGetProperty("word", out JsonElement word) || !item.TryGetProperty("start", out JsonElement start) || !item.TryGetProperty("end", out JsonElement end)) continue;
                words.Add(new(word.GetString() ?? "", start.GetDouble(), end.GetDouble()));
            }
        }
        return new(text, confidence, words);
    }
}
