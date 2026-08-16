namespace MetaVoiceType.Core.Interfaces;

public sealed record ModelDownloadProgress(string Stage, long BytesDownloaded, long? TotalBytes, double BytesPerSecond)
{
    public double? Percentage => TotalBytes is > 0 ? 100d * BytesDownloaded / TotalBytes : null;
}

public sealed record ModelInstallRequest(Uri ArchiveUrl, string ArchiveType, string ExpectedDirectory, string DestinationRoot,
    string? ArchiveSha256, long? ExpectedBytes, IReadOnlyList<string> RequiredFiles);

public interface IModelDownloadService
{
    Task<string> InstallAsync(ModelInstallRequest request, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default);
}
