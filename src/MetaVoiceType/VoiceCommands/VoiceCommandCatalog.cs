using System.Reflection;
using System.Text.Json;
using MetaVoiceType.Core.Models;

namespace MetaVoiceType.VoiceCommands;

public sealed record VoiceCommandLanguage(
    string Id,
    string DisplayName,
    string ModelName,
    Uri ArchiveUrl,
    string ArchiveType,
    string License,
    string RestrictedGrammar,
    long? SizeBytes,
    IReadOnlyDictionary<string, string> Commands);

public sealed record VoiceCommandCatalog(int SchemaVersion, string DefaultLanguage, IReadOnlyList<VoiceCommandLanguage> Languages)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public VoiceCommandLanguage Get(string id) => Languages.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown voice-command language '{id}'.");

    public static VoiceCommandCatalog LoadBundled()
    {
        Assembly assembly = typeof(VoiceCommandCatalog).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(x => x.EndsWith("voice-command-languages.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("Bundled voice-command catalog is missing.");
        var catalog = JsonSerializer.Deserialize<VoiceCommandCatalog>(stream, JsonOptions)
            ?? throw new InvalidDataException("Voice-command catalog is empty.");
        catalog.Validate();
        return catalog;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || Languages.Count == 0) throw new InvalidDataException("Unsupported or empty catalog.");
        if (Languages.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Languages.Count)
            throw new InvalidDataException("Duplicate language IDs exist.");
        if (!Languages.Any(x => x.Id.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Default language does not exist.");
        foreach (VoiceCommandLanguage language in Languages)
        {
            if (!language.ArchiveUrl.IsAbsoluteUri || language.ArchiveUrl.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"Invalid archive URL for {language.Id}.");
            if (!language.ModelName.Contains("small", StringComparison.OrdinalIgnoreCase) && language.Id != "uk")
                throw new InvalidDataException($"Non-small model is only permitted for Ukrainian ({language.Id}).");
            CommandPhraseValidator.Validate(language.Commands);
        }
    }
}

public static class CommandPhraseValidator
{
    public static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public static void Validate(IReadOnlyDictionary<string, string> phrases)
    {
        string[] required = VoiceCommandKeys.All.Values.ToArray();
        if (required.Any(x => !phrases.ContainsKey(x))) throw new InvalidDataException("A required command phrase is missing.");
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string phrase in required.Select(x => Normalize(phrases[x])))
        {
            if (phrase.Length == 0 || phrase == "[unk]") throw new InvalidDataException("Command phrases cannot be blank or [unk].");
            if (!normalized.Add(phrase)) throw new InvalidDataException("Command phrases must be unique within a language.");
        }
    }
}
