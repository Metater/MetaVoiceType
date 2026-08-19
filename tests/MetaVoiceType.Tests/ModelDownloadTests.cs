using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MetaVoiceType.Tests;

public sealed class ModelDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MetaVoiceType.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VerifiedArchiveCommitsOnlyAfterRequiredFilesExist()
    {
        byte[] zip = Zip(("model/am/final.mdl", "model"), ("model/conf/mfcc.conf", "config"));
        var service = Service(zip);
        var request = new ModelInstallRequest(new("https://example.test/model.zip"), "zip", "model", _root,
            Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, ["am/final.mdl", "conf/mfcc.conf"]);

        string installed = await service.InstallAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_root, "model"), installed);
        Assert.Equal("model", await File.ReadAllTextAsync(Path.Combine(installed, "am", "final.mdl"), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root), x => Path.GetFileName(x).StartsWith(".install-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HashMismatchAndZipTraversalNeverCommit()
    {
        byte[] valid = Zip(("model/am/final.mdl", "model"));
        var badHash = new ModelInstallRequest(new("https://example.test/model.zip"), "zip", "model", _root, new string('0', 64), valid.Length, ["am/final.mdl"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(valid).InstallAsync(badHash, cancellationToken: TestContext.Current.CancellationToken));

        byte[] traversal = Zip(("../escaped.txt", "no"), ("model/am/final.mdl", "model"));
        var unsafeArchive = badHash with { ArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(traversal)), ExpectedBytes = traversal.Length };
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(traversal).InstallAsync(unsafeArchive, cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(Directory.GetParent(_root)!.FullName, "escaped.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "model")));
    }

    [Fact]
    public async Task InterruptedExtractionIsRecoveredWithoutDownloadingAgain()
    {
        string abandoned = Path.Combine(_root, ".install-interrupted", "model", "am");
        Directory.CreateDirectory(abandoned);
        await File.WriteAllTextAsync(Path.Combine(abandoned, "final.mdl"), "recovered", TestContext.Current.CancellationToken);
        var handler = new CountingHandler();
        var service = new ModelDownloadService(new HttpClient(handler), NullLogger<ModelDownloadService>.Instance);
        var request = new ModelInstallRequest(new("https://example.test/model.zip"), "zip", "model", _root,
            new string('0', 64), 1, ["am/final.mdl"]);

        string installed = await service.InstallAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("recovered", await File.ReadAllTextAsync(Path.Combine(installed, "am", "final.mdl"), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Requests);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root), x => Path.GetFileName(x).StartsWith(".install-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProgressUsesOverallCheckpointsAndIndeterminateLongRunningStages()
    {
        byte[] zip = Zip(("model/am/final.mdl", "model"));
        var request = new ModelInstallRequest(new("https://example.test/model.zip"), "zip", "model", _root,
            Convert.ToHexStringLower(SHA256.HashData(zip)), zip.Length, ["am/final.mdl"]);
        var values = new List<ModelDownloadProgress>();

        await Service(zip).InstallAsync(request, new InlineProgress<ModelDownloadProgress>(values.Add), TestContext.Current.CancellationToken);

        Assert.Equal(100, values[^1].OverallPercentage);
        Assert.Contains(values, value => value.Stage == "Verifying download" && value.IsIndeterminate);
        Assert.Contains(values, value => value.Stage.StartsWith("Extracting", StringComparison.Ordinal) && value.IsIndeterminate);
        Assert.True(values.Where(value => value.OverallPercentage is not null).Select(value => value.OverallPercentage!.Value).All(value => value is >= 0 and <= 100));
    }

    private static ModelDownloadService Service(byte[] response) => new(new HttpClient(new StaticHandler(response)), NullLogger<ModelDownloadService>.Instance);

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach ((string path, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class StaticHandler(byte[] response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(response) });
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
