using System.Text.Json;
using MetaVoiceType.Core.Models;

namespace MetaVoiceType.VoiceCommands;

public sealed record VoiceCommandMatch(VoiceCommand Command, string Phrase, int Position);

public static class VoskResultMatcher
{
    public static IReadOnlyList<VoiceCommandMatch> Match(string resultJson, IReadOnlyDictionary<VoiceCommand, string> configured)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return [];
        using JsonDocument document = JsonDocument.Parse(resultJson);
        var alternatives = new List<string>();
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("alternatives", out JsonElement values))
        {
            foreach (JsonElement item in values.EnumerateArray())
                alternatives.Add(item.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "");
        }
        else if (root.TryGetProperty("text", out JsonElement single))
            alternatives.Add(single.GetString() ?? "");

        foreach (string raw in alternatives)
        {
            string text = CommandPhraseValidator.Normalize(raw);
            if (text.Length == 0 || text == "[unk]") continue;
            var matches = new List<VoiceCommandMatch>();
            foreach ((VoiceCommand command, string rawPhrase) in configured)
            {
                string phrase = CommandPhraseValidator.Normalize(rawPhrase);
                int offset = 0;
                while ((offset = text.IndexOf(phrase, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    bool left = offset == 0 || char.IsWhiteSpace(text[offset - 1]);
                    int end = offset + phrase.Length;
                    bool right = end == text.Length || char.IsWhiteSpace(text[end]);
                    if (left && right) matches.Add(new(command, phrase, offset));
                    offset = Math.Max(end, offset + 1);
                }
            }

            if (matches.Count == 0) continue;
            return matches
                .Where(candidate => !matches.Any(other => other.Position == candidate.Position && other.Phrase.Length > candidate.Phrase.Length
                    && other.Phrase.Contains(candidate.Phrase, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Position).ThenByDescending(x => x.Phrase.Length).ToArray();
        }
        return [];
    }
}
