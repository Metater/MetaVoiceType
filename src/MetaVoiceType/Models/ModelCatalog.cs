using System.Reflection;
using System.Text.Json;

namespace MetaVoiceType.Models;

public sealed record ModelFileSet(string Encoder, string Decoder, string Joiner, string Tokens);
public sealed record ModelLanguageCapabilities(bool AutomaticDetection, IReadOnlyList<string> TranscriptionReady, IReadOnlyList<string> BroadCoverage, IReadOnlyList<string> AdaptationReady);
public sealed record DictationModel(string Id, string DisplayName, Uri ArchiveUrl, string ArchiveType, string ExtractedDirectory,
    string ArchiveSha256, long EstimatedDownloadBytes, IReadOnlyList<string> RequiredFiles, string License, Uri LicenseUrl,
    string DefaultLanguage, ModelFileSet Files, ModelLanguageCapabilities Languages)
{
    public void Validate()
    {
        if (!ArchiveUrl.IsAbsoluteUri || ArchiveUrl.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Nemotron archive URL must use HTTPS.");
        if (ArchiveType != "tar.bz2") throw new InvalidDataException("Nemotron archive type is unsupported.");
        if (ArchiveSha256.Length != 64 || ArchiveSha256.Any(x => !Uri.IsHexDigit(x))) throw new InvalidDataException("Nemotron SHA-256 is malformed.");
        if (EstimatedDownloadBytes <= 0) throw new InvalidDataException("Nemotron download size is missing.");
        if (DefaultLanguage != "auto" || !Languages.AutomaticDetection) throw new InvalidDataException("Nemotron must default to automatic language detection.");
        string[] files = [Files.Encoder, Files.Decoder, Files.Joiner, Files.Tokens];
        if (files.Any(string.IsNullOrWhiteSpace) || files.Any(x => !RequiredFiles.Contains(x, StringComparer.Ordinal)))
            throw new InvalidDataException("Nemotron required files are incomplete.");
    }
}
public sealed record ModelCatalog(int SchemaVersion, DictationModel Nemotron)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static ModelCatalog LoadBundled()
    {
        Assembly assembly = typeof(ModelCatalog).Assembly;
        string name = assembly.GetManifestResourceNames().Single(x => x.EndsWith("model-catalog.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        ModelCatalog catalog = JsonSerializer.Deserialize<ModelCatalog>(stream, JsonOptions)
            ?? throw new InvalidDataException("Model catalog is empty.");
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("Unsupported model catalog version.");
        catalog.Nemotron.Validate();
        return catalog;
    }
}
