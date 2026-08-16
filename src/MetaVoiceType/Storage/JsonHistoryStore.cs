using System.Text.Json;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.Storage;

public sealed partial class JsonHistoryStore(AppPaths paths, ILogger<JsonHistoryStore> logger) : IHistoryStore, IDisposable
{
    public const int Retention = 100;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<TranscriptRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = (await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false)).ToList();
            int existing = records.FindIndex(x => x.LogicalId.Equals(record.LogicalId, StringComparison.Ordinal));
            if (existing >= 0) records.RemoveAt(existing);
            records.Insert(0, record);
            if (records.Count > Retention)
                records.RemoveRange(Retention, records.Count - Retention);
            await AtomicJsonFile.WriteAsync(paths.HistoryFile, records, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string logicalTranscriptId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalTranscriptId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = (await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false)).Where(x =>
                !x.LogicalId.Equals(logicalTranscriptId, StringComparison.Ordinal)).ToList();
            await AtomicJsonFile.WriteAsync(paths.HistoryFile, records, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<TranscriptRecord>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.HistoryFile)) return [];
        try
        {
            await using var stream = File.OpenRead(paths.HistoryFile);
            return await JsonSerializer.DeserializeAsync<List<TranscriptRecord>>(stream, AtomicJsonFile.Options, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            LogUnreadable(logger, ex);
            return [];
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "History was unreadable; an empty history will be used.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);
}
