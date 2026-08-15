using System.Text.Json;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>One completed transcript in persistent history (text only).</summary>
public sealed record HistoryEntry(
    string SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? StoppedAtUtc,
    double DurationSeconds,
    string Status,
    string Language,
    string Transcript,
    bool IsCanceled,
    bool WasPasted,
    bool WasCopied)
{
    /// <summary>Text length used for tests (never persisted separately).</summary>
    public int TranscriptLength => Transcript.Length;
}

/// <summary>
/// Persistent text-only history store using atomic System.Text.Json writes.
/// Chosen over LiteDB after evaluation: this store is a single append-style
/// list of ≤100 records, and atomic file-replace semantics are simpler and
/// more auditable than an embedded database for that shape. LiteDB remains a
/// reasonable upgrade path if richer queries are needed later.
/// </summary>
public sealed class HistoryStore
{
    public const int DefaultLimit = 100;

    private readonly string _path;
    private readonly int _limit;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<HistoryEntry>? _cache;

    public HistoryStore(string path, int limit = DefaultLimit, ILogger? log = null)
    {
        _path = path;
        _limit = limit;
        _log = log ?? NullLogger.Instance;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>Load history from disk (empty if missing or corrupt).</summary>
    public async Task<List<HistoryEntry>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null)
                return new List<HistoryEntry>(_cache);
            if (!File.Exists(_path))
            {
                _cache = new List<HistoryEntry>();
                return _cache;
            }
            try
            {
                string json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
                _cache = JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();
                _cache = _cache.OrderBy(e => e.StartedAtUtc).ToList();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "History file corrupt or unreadable; starting with empty history.");
                _cache = new List<HistoryEntry>();
            }
            return new List<HistoryEntry>(_cache);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Append a completed entry and persist atomically (temp file + rename).
    /// Oldest eligible COMPLETED entries are pruned beyond the limit.
    /// </summary>
    public async Task AppendAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<HistoryEntry> entries = _cache ?? await LoadCoreAsync(ct).ConfigureAwait(false);
            entries.Add(entry);
            Prune(entries);
            await PersistCoreAsync(entries, ct).ConfigureAwait(false);
            _cache = entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replace the full list (used by tests and recovery).</summary>
    public async Task ReplaceAsync(List<HistoryEntry> entries, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Prune(entries);
            await PersistCoreAsync(entries, ct).ConfigureAwait(false);
            _cache = entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Prune(List<HistoryEntry> entries)
    {
        if (entries.Count <= _limit)
            return;
        // Oldest first; remove oldest eligible completed entries only.
        var ordered = entries.OrderBy(e => e.StartedAtUtc).ToList();
        var kept = new List<HistoryEntry>();
        int removeNeeded = entries.Count - _limit;
        foreach (var e in ordered)
        {
            bool eligible = e.Status is "Completed" or "Canceled";
            if (removeNeeded > 0 && eligible)
            {
                removeNeeded--;
                continue;
            }
            kept.Add(e);
        }
        entries.Clear();
        entries.AddRange(kept.OrderBy(e => e.StartedAtUtc));
    }

    private async Task<List<HistoryEntry>> LoadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return new List<HistoryEntry>();
        string json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new List<HistoryEntry>();
    }

    private async Task PersistCoreAsync(List<HistoryEntry> entries, CancellationToken ct)
    {
        string tmp = _path + ".tmp";
        string json = JsonSerializer.Serialize(entries, JsonOptions);
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 8192, useAsync: true))
        {
            await fs.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }
        File.Move(tmp, _path, overwrite: true);
        _log.LogInformation("History persisted: {Count} entries -> {Path}", entries.Count, _path);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) { }
    }
}
