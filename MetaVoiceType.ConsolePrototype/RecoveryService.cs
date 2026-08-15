using System.Text.Json;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Discovers incomplete recovery sessions at startup and replays their
/// temporary PCM through the SAME ASR pipeline used for live recording.
/// A recovered session is just another finalize work item fed from a file.
/// </summary>
public sealed class RecoveryService
{
    private readonly string _directory;
    private readonly ILogger _log;

    public RecoveryService(string directory, ILogger log)
    {
        _directory = directory;
        _log = log;
    }

    public string Directory => _directory;

    /// <summary>List session IDs that have recovery PCM on disk.</summary>
    public IEnumerable<string> Discover()
    {
        if (!System.IO.Directory.Exists(_directory))
            return Array.Empty<string>();
        return System.IO.Directory.EnumerateFiles(_directory, "*.pcm")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(id => id)
            .ToList();
    }

    public async Task<RecoveryMetadata?> ReadMetadataAsync(string sessionId, CancellationToken ct)
    {
        string metaPath = Path.Combine(_directory, $"{sessionId}.json");
        if (!File.Exists(metaPath))
            return null;
        try
        {
            string json = await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RecoveryMetadata>(json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Recovery metadata for {Id} unreadable; will infer from PCM.", sessionId);
            return null;
        }
    }

    /// <summary>Stream PCM16 samples from a recovery file at real-time pace.</summary>
    public async IAsyncEnumerable<float[]> ReadPcmFramesAsync(string sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string path = Path.Combine(_directory, $"{sessionId}.pcm");
        byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        int sampleCount = bytes.Length / 2;
        int chunkSamples = 16000 / 50; // 20 ms frames
        for (int offset = 0; offset < sampleCount && !ct.IsCancellationRequested; offset += chunkSamples)
        {
            int n = Math.Min(chunkSamples, sampleCount - offset);
            var frame = new float[n];
            for (int i = 0; i < n; i++)
            {
                short s = (short)(bytes[(offset + i) * 2] | (bytes[(offset + i) * 2 + 1] << 8));
                frame[i] = s / 32768f;
            }
            yield return frame;
        }
        _log.LogInformation("Recovery PCM replay complete for {Id}: {Samples} samples.",
            sessionId, sampleCount);
    }

    /// <summary>Delete recovery audio and metadata after a durable commit.</summary>
    public void DeleteRecoveryFiles(string sessionId)
    {
        string pcm = Path.Combine(_directory, $"{sessionId}.pcm");
        string meta = Path.Combine(_directory, $"{sessionId}.json");
        if (File.Exists(pcm))
        {
            File.Delete(pcm);
            _log.LogInformation("Recovery PCM deleted for {Id}.", sessionId);
        }
        if (File.Exists(meta))
        {
            File.Delete(meta);
            _log.LogInformation("Recovery metadata deleted for {Id}.", sessionId);
        }
    }
}
