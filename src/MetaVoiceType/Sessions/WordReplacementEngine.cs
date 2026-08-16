using System.Text;
using MetaVoiceType.Core.Models;

namespace MetaVoiceType.Sessions;

public static class WordReplacementEngine
{
    public static string Apply(string text, IEnumerable<WordReplacement> configured)
    {
        if (string.IsNullOrEmpty(text)) return text;
        WordReplacement[] rules = configured
            .Where(x => !string.IsNullOrWhiteSpace(x.Match))
            .OrderByDescending(x => x.Match.Length)
            .ThenBy(x => x.Match, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .ToArray();
        if (rules.Length == 0) return text;

        var output = new StringBuilder(text.Length);
        int position = 0;
        while (position < text.Length)
        {
            WordReplacement? match = rules.FirstOrDefault(rule =>
                position + rule.Match.Length <= text.Length &&
                text.AsSpan(position, rule.Match.Length).Equals(rule.Match.AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                IsBoundary(text, position - 1) && IsBoundary(text, position + rule.Match.Length));
            if (match is null) output.Append(text[position++]);
            else { output.Append(match.Replacement); position += match.Match.Length; }
        }
        return output.ToString();
    }

    public static void Validate(WordReplacement replacement)
    {
        if (string.IsNullOrWhiteSpace(replacement.Match)) throw new InvalidDataException("Replacement match text cannot be empty.");
    }

    private static bool IsBoundary(string text, int index) => index < 0 || index >= text.Length || !IsWordCharacter(text[index]);
    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}
