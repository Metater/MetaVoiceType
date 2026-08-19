using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using MetaVoiceType.Core.Interfaces;
using Microsoft.Extensions.Logging;
using SharpCompress.Readers;

namespace MetaVoiceType.Models;

public sealed partial class ModelDownloadService(HttpClient httpClient, ILogger<ModelDownloadService> logger) : IModelDownloadService
{
    public async Task<string> InstallAsync(ModelInstallRequest request, IProgress<ModelDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        Directory.CreateDirectory(request.DestinationRoot);
        string final = Path.Combine(request.DestinationRoot, request.ExpectedDirectory);
        if (IsValidInstallation(final, request.RequiredFiles)) return final;
        if (RecoverAbandonedInstallation(request, final)) return final;
        string work = Path.Combine(request.DestinationRoot, ".install-" + Guid.NewGuid().ToString("N"));
        string archive = work + ".part";
        try
        {
            progress?.Report(new("Preparing download", 0, request.ExpectedBytes, 0, 1, true));
            Directory.CreateDirectory(work);
            await File.WriteAllTextAsync(Path.Combine(work, ".artifact"), request.ExpectedDirectory, cancellationToken).ConfigureAwait(false);
            await DownloadAsync(request.ArchiveUrl, archive, request.ExpectedBytes, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("Verifying download", new FileInfo(archive).Length, request.ExpectedBytes, 0, 72, true));
            await VerifySha256Async(archive, request.ArchiveSha256!, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("Download verified", new FileInfo(archive).Length, request.ExpectedBytes, 0, 76));
            progress?.Report(new("Extracting files", 0, null, 0, 80, true));
            if (request.ArchiveType.Equals("zip", StringComparison.OrdinalIgnoreCase)) await ExtractZipAsync(archive, work, progress, cancellationToken).ConfigureAwait(false);
            else if (request.ArchiveType.Equals("tar.bz2", StringComparison.OrdinalIgnoreCase)) await ExtractArchiveAsync(archive, work, progress, cancellationToken).ConfigureAwait(false);
            else if (request.ArchiveType.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                if (request.RequiredFiles.Count != 1) throw new InvalidDataException("Direct-file artifacts must declare exactly one required file.");
                string extractedDirectory = Path.Combine(work, request.ExpectedDirectory);
                Directory.CreateDirectory(extractedDirectory);
                File.Move(archive, SafeTarget(extractedDirectory, request.RequiredFiles[0]));
            }
            else throw new InvalidDataException($"Unsupported archive type '{request.ArchiveType}'.");
            string extracted = Path.Combine(work, request.ExpectedDirectory);
            progress?.Report(new("Validating installed files", 0, null, 0, 94, true));
            if (!IsValidInstallation(extracted, request.RequiredFiles)) throw new InvalidDataException($"Archive did not contain a valid '{request.ExpectedDirectory}' installation.");
            progress?.Report(new("Activating installation", 0, null, 0, 98, true));
            CommitDirectory(extracted, final);
            progress?.Report(new("Installed", request.ExpectedBytes ?? 0, request.ExpectedBytes, 0, 100));
            LogInstalled(logger, request.ExpectedDirectory);
            return final;
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(work)) Directory.Delete(work, true);
        }
    }

    private static bool RecoverAbandonedInstallation(ModelInstallRequest request, string final)
    {
        foreach (string backup in Directory.EnumerateDirectories(request.DestinationRoot,
                     request.ExpectedDirectory + ".previous-*", SearchOption.TopDirectoryOnly))
        {
            if (!IsValidInstallation(final, request.RequiredFiles) && IsValidInstallation(backup, request.RequiredFiles))
            {
                if (Directory.Exists(final)) Directory.Delete(final, true);
                Directory.Move(backup, final);
            }
            else Directory.Delete(backup, true);
        }

        foreach (string work in Directory.EnumerateDirectories(request.DestinationRoot, ".install-*", SearchOption.TopDirectoryOnly))
        {
            string extracted = Path.Combine(work, request.ExpectedDirectory);
            string marker = Path.Combine(work, ".artifact");
            bool belongsToRequest = Directory.Exists(extracted) || File.Exists(marker) &&
                string.Equals(File.ReadAllText(marker).Trim(), request.ExpectedDirectory, StringComparison.Ordinal);
            if (!belongsToRequest) continue;
            if (!IsValidInstallation(final, request.RequiredFiles) && IsValidInstallation(extracted, request.RequiredFiles))
                CommitDirectory(extracted, final);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            string archive = work + ".part";
            if (File.Exists(archive)) File.Delete(archive);
        }
        return IsValidInstallation(final, request.RequiredFiles);
    }

    private static void CommitDirectory(string extracted, string final)
    {
        string? backup = null;
        try
        {
            if (Directory.Exists(final))
            {
                backup = final + ".previous-" + Guid.NewGuid().ToString("N");
                Directory.Move(final, backup);
            }
            Directory.Move(extracted, final);
            if (backup is not null) Directory.Delete(backup, true);
        }
        catch
        {
            if (!Directory.Exists(final) && backup is not null && Directory.Exists(backup)) Directory.Move(backup, final);
            throw;
        }
    }

    private static void ValidateRequest(ModelInstallRequest request)
    {
        if (request.ArchiveSha256 is not { Length: 64 } digest || digest.Any(x => !Uri.IsHexDigit(x))) throw new ArgumentException("A valid SHA-256 pin is required.", nameof(request));
        if (request.ExpectedBytes is not > 0) throw new ArgumentException("A positive expected byte count is required.", nameof(request));
        if (request.RequiredFiles.Count == 0 || request.RequiredFiles.Any(Path.IsPathRooted)) throw new ArgumentException("Relative required files are required.", nameof(request));
    }

    private static bool IsValidInstallation(string directory, IReadOnlyList<string> requiredFiles) => Directory.Exists(directory) && requiredFiles.All(file => File.Exists(SafeTarget(directory, file)) && new FileInfo(SafeTarget(directory, file)).Length > 0);

    private static async Task VerifySha256Async(string path, string expected, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        string actual = Convert.ToHexStringLower(digest);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Archive SHA-256 mismatch. Expected {expected}, got {actual}.");
    }

    private async Task DownloadAsync(Uri url, string path, long? expectedBytes, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new("Connecting", 0, expectedBytes, 0, 3, true));
        using HttpResponseMessage response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? responseBytes = response.Content.Headers.ContentLength;
        if (expectedBytes is > 0 && responseBytes is > 0 && expectedBytes != responseBytes)
            throw new InvalidDataException($"Download size metadata mismatch ({responseBytes}/{expectedBytes} bytes).");
        long? total = expectedBytes is > 0 ? expectedBytes : responseBytes;
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        byte[] buffer = new byte[128 * 1024];
        long read = 0;
        var clock = Stopwatch.StartNew();
        long lastProgressMilliseconds = -250;
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            read += count;
            if (clock.ElapsedMilliseconds - lastProgressMilliseconds >= 250 || read == total)
            {
                lastProgressMilliseconds = clock.ElapsedMilliseconds;
                double? overall = total is > 0 ? 5 + (65d * read / total.Value) : null;
                progress?.Report(new("Downloading", read, total, read / Math.Max(clock.Elapsed.TotalSeconds, 0.01), overall, total is null));
            }
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (total is not null && read != total) throw new InvalidDataException($"Download was incomplete ({read}/{total} bytes).");
    }

    private static async Task ExtractZipAsync(string archivePath, string destination, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = SafeTarget(destination, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using Stream input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
            await CopyEntryAsync(input, output, entry.Length, entry.FullName, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExtractArchiveAsync(string archivePath, string destination, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(archivePath);
        using IReader reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
        while (reader.MoveToNextEntry())
        {
            var entry = reader.Entry;
            if (entry.IsDirectory) continue;
            cancellationToken.ThrowIfCancellationRequested();
            string target = SafeTarget(destination, entry.Key ?? throw new InvalidDataException("Archive entry has no name."));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using Stream input = reader.OpenEntryStream();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
            await CopyEntryAsync(input, output, entry.Size, entry.Key ?? "archive entry", progress, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyEntryAsync(Stream input, Stream output, long totalBytes, string name,
        IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[128 * 1024];
        long written = 0;
        var clock = Stopwatch.StartNew();
        long lastProgressMilliseconds = -250;
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            written += count;
            if (clock.ElapsedMilliseconds - lastProgressMilliseconds >= 250 || written == totalBytes)
            {
                lastProgressMilliseconds = clock.ElapsedMilliseconds;
                progress?.Report(new($"Extracting {Path.GetFileName(name)}", written, totalBytes > 0 ? totalBytes : null,
                    written / Math.Max(clock.Elapsed.TotalSeconds, 0.01), 80, true));
            }
        }
    }

    private static string SafeTarget(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive entry attempted path traversal.");
        return target;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Installed model {ModelName}.")]
    private static partial void LogInstalled(ILogger logger, string modelName);
}
