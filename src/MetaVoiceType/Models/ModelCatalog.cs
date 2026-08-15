using System.Reflection;
using System.Text.Json;

namespace MetaVoiceType.Models;

public static class ModelArtifactKinds
{
    public const string Dictation = "dictation";
    public const string Vad = "vad";
    public const string Runtime = "runtime";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([Dictation, Vad, Runtime], StringComparer.Ordinal);
}

public sealed record ArtifactFiles(
    string? Encoder = null,
    string? Decoder = null,
    string? Joiner = null,
    string? Tokens = null,
    string? Model = null,
    string? NativeLibrary = null,
    string? LibraryDirectory = null);

public sealed record ArtifactCapabilities(
    bool AutomaticLanguageDetection = false,
    IReadOnlyList<string>? Languages = null,
    string? DictationMode = null);

public sealed record ModelArtifact(
    string Id,
    string Kind,
    string DisplayName,
    Uri ArchiveUrl,
    string ArchiveType,
    string ExpectedDirectory,
    string ArchiveSha256,
    long EstimatedDownloadBytes,
    IReadOnlyList<string> RequiredFiles,
    string License,
    Uri LicenseUrl,
    ArtifactFiles Files,
    ArtifactCapabilities? Capabilities = null,
    string? DefaultLanguage = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || !Id.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_'))
            throw new InvalidDataException("A model artifact id is malformed.");
        if (!ModelArtifactKinds.All.Contains(Kind)) throw new InvalidDataException($"Artifact '{Id}' has unsupported kind '{Kind}'.");
        if (!ArchiveUrl.IsAbsoluteUri || ArchiveUrl.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException($"Artifact '{Id}' must use an HTTPS URL.");
        if (ArchiveType is not ("tar.bz2" or "zip" or "file")) throw new InvalidDataException($"Artifact '{Id}' has unsupported archive type '{ArchiveType}'.");
        if (Path.IsPathRooted(ExpectedDirectory) || ExpectedDirectory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ExpectedDirectory is "." or "..")
            throw new InvalidDataException($"Artifact '{Id}' has an unsafe expected directory.");
        if (ArchiveSha256.Length != 64 || ArchiveSha256.Any(x => !Uri.IsHexDigit(x))) throw new InvalidDataException($"Artifact '{Id}' SHA-256 is malformed.");
        if (EstimatedDownloadBytes <= 0) throw new InvalidDataException($"Artifact '{Id}' download size is missing.");
        if (RequiredFiles.Count == 0 || RequiredFiles.Any(string.IsNullOrWhiteSpace) || RequiredFiles.Any(Path.IsPathRooted))
            throw new InvalidDataException($"Artifact '{Id}' required files are incomplete.");
        if (string.IsNullOrWhiteSpace(License) || !LicenseUrl.IsAbsoluteUri) throw new InvalidDataException($"Artifact '{Id}' license metadata is incomplete.");

        string[] declaredFiles = new string?[] { Files.Encoder, Files.Decoder, Files.Joiner, Files.Tokens, Files.Model, Files.NativeLibrary }
            .OfType<string>().Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (declaredFiles.Any(x => !RequiredFiles.Contains(x, StringComparer.Ordinal)))
            throw new InvalidDataException($"Artifact '{Id}' file map is inconsistent with requiredFiles.");
        if (Kind == ModelArtifactKinds.Dictation &&
            (string.IsNullOrWhiteSpace(Files.Encoder) || string.IsNullOrWhiteSpace(Files.Decoder) || string.IsNullOrWhiteSpace(Files.Joiner) || string.IsNullOrWhiteSpace(Files.Tokens)))
            throw new InvalidDataException($"Dictation artifact '{Id}' is missing transducer files.");
        if (Kind == ModelArtifactKinds.Vad && string.IsNullOrWhiteSpace(Files.Model)) throw new InvalidDataException($"VAD artifact '{Id}' is missing its model file.");
        if (Kind == ModelArtifactKinds.Runtime && (string.IsNullOrWhiteSpace(Files.NativeLibrary) || string.IsNullOrWhiteSpace(Files.LibraryDirectory)))
            throw new InvalidDataException($"Runtime artifact '{Id}' is missing native-library metadata.");
    }

    public Core.Interfaces.ModelInstallRequest ToInstallRequest(string destinationRoot) =>
        new(ArchiveUrl, ArchiveType, ExpectedDirectory, destinationRoot, ArchiveSha256, EstimatedDownloadBytes, RequiredFiles);
}

public sealed record ModelCatalog(int SchemaVersion, IReadOnlyList<ModelArtifact> Artifacts)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ModelArtifact Get(string id) => Artifacts.FirstOrDefault(x => x.Id.Equals(id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Unknown model artifact '{id}'.");

    public static ModelCatalog LoadBundled()
    {
        Assembly assembly = typeof(ModelCatalog).Assembly;
        string name = assembly.GetManifestResourceNames().Single(x => x.EndsWith("model-catalog.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        ModelCatalog catalog = JsonSerializer.Deserialize<ModelCatalog>(stream, JsonOptions)
            ?? throw new InvalidDataException("Model catalog is empty.");
        if (catalog.SchemaVersion != 2) throw new InvalidDataException("Unsupported model catalog version.");
        if (catalog.Artifacts.Count == 0) throw new InvalidDataException("Model catalog contains no artifacts.");
        foreach (ModelArtifact artifact in catalog.Artifacts) artifact.Validate();
        if (catalog.Artifacts.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Artifacts.Count)
            throw new InvalidDataException("Model catalog contains duplicate ids.");
        foreach (string required in new[] { "parakeet-v2", "parakeet-v3", "silero-vad", "sherpa-cuda-12" }) catalog.Get(required);
        return catalog;
    }
}
