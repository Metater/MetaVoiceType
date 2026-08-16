using System.Reflection;
using System.Text.Json;
using MetaVoiceType.Core.Models;

namespace MetaVoiceType.VoiceCommands;

public sealed record VoiceCommandLanguage(
    string Id,
    string DisplayName,
    string ModelName,
    string Repository,
    string ReleaseTag,
    string AssetName,
    long? AssetId,
    Uri ArchiveUrl,
    string ArchiveType,
    string ArchiveSha256,
    long ArchiveBytes,
    IReadOnlyList<string> RequiredFiles,
    string License,
    string RestrictedGrammar,
    IReadOnlyDictionary<string, string> Commands,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? CommandAliases = null)
{
    public long SizeBytes => ArchiveBytes;

    public Core.Interfaces.ModelInstallRequest ToInstallRequest(string destinationRoot) =>
        new(ArchiveUrl, ArchiveType, ModelName, destinationRoot, ArchiveSha256, ArchiveBytes, RequiredFiles);
}

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
        if (SchemaVersion != 2 || Languages.Count == 0) throw new InvalidDataException("Unsupported or empty catalog.");
        if (Languages.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Languages.Count)
            throw new InvalidDataException("Duplicate language IDs exist.");
        if (!Languages.Any(x => x.Id.Equals(DefaultLanguage, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Default language does not exist.");
        foreach (VoiceCommandLanguage language in Languages)
        {
            if (!language.ArchiveUrl.IsAbsoluteUri || language.ArchiveUrl.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException($"Invalid archive URL for {language.Id}.");
            if (string.IsNullOrWhiteSpace(language.Repository) || string.IsNullOrWhiteSpace(language.ReleaseTag) ||
                string.IsNullOrWhiteSpace(language.AssetName) || !language.ArchiveUrl.AbsolutePath.EndsWith('/' + language.AssetName, StringComparison.Ordinal) ||
                language.ArchiveSha256.Length != 64 || language.ArchiveSha256.Any(x => !Uri.IsHexDigit(x)) || language.ArchiveBytes <= 0 ||
                language.RequiredFiles.Count == 0)
                throw new InvalidDataException($"Incomplete deterministic artifact pin for {language.Id}.");
            if (!language.ModelName.Contains("small", StringComparison.OrdinalIgnoreCase) && language.Id != "uk")
                throw new InvalidDataException($"Non-small model is only permitted for Ukrainian ({language.Id}).");
            CommandPhraseValidator.Validate(language.Commands);
            if (language.CommandAliases is not null)
                foreach ((string command, IReadOnlyList<string> aliases) in language.CommandAliases)
                {
                    if (!VoiceCommandKeys.All.Values.Contains(command, StringComparer.Ordinal))
                        throw new InvalidDataException($"Unknown command alias key '{command}' for {language.Id}.");
                    CommandPhraseValidator.ValidateAliases(aliases);
                }
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

    public static void ValidateAliases(IEnumerable<string> aliases)
    {
        string[] normalized = aliases.Select(Normalize).ToArray();
        if (normalized.Length == 0 || normalized.Any(x => x.Length == 0 || x == "[unk]"))
            throw new InvalidDataException("Each command needs at least one non-empty alias.");
        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new InvalidDataException("Command aliases must be unique.");
    }
}
