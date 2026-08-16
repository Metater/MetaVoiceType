using System.Text.Json;
using System.Threading.Channels;
using System.Collections.Concurrent;
using MetaVoiceType.Audio;
using MetaVoiceType.Storage;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.Sessions;

public sealed record RecoveryMetadata(string SessionId, string Language, DateTimeOffset StartedAt, long SamplesWritten, string State,
    string? LogicalTranscriptId = null, string? PreviousText = null, int PriorSegmentCount = 0, double PriorDurationSeconds = 0,
    DateTimeOffset? LogicalStartedAt = null);

public sealed partial class RecoveryWriter(AppPaths paths, ILogger<RecoveryWriter> logger) : IAsyncDisposable
{
    private sealed record Work(string SessionId, string Language, DateTimeOffset StartedAt, DateTimeOffset LogicalStartedAt, string LogicalTranscriptId,
        string PreviousText, int PriorSegmentCount, double PriorDurationSeconds, AudioFrame? Frame, bool Close, TaskCompletionSource? Completion = null);
    private sealed class OpenState(FileStream stream, RecoveryMetadata metadata)
    {
        public FileStream Stream { get; } = stream;
        public RecoveryMetadata Metadata { get; set; } = metadata;
        public DateTimeOffset LastFlush { get; set; } = DateTimeOffset.UtcNow;
    }
    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _activeWorker;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pendingCloses = new(StringComparer.Ordinal);
    private int _depth;
    private int _maxDepth;
    public int QueueDepth => Volatile.Read(ref _depth);
    public int MaxQueueDepth => Volatile.Read(ref _maxDepth);

    public void Start() { paths.EnsureCreated(); _activeWorker ??= Task.Run(() => RunAsync(_lifetime.Token)); }
    public void Enqueue(DictationSession session, AudioFrame frame) { if (_queue.Writer.TryWrite(CreateWork(session, frame, false))) UpdateDepth(); }
    public Task CloseAsync(DictationSession session)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_activeWorker?.IsCompleted == true) completion.SetException(new InvalidOperationException("The recovery writer stopped unexpectedly."));
        else if (!_pendingCloses.TryAdd(session.Id, completion)) completion.SetException(new InvalidOperationException("This recovery session is already closing."));
        else if (_queue.Writer.TryWrite(CreateWork(session, null, true, completion))) UpdateDepth();
        else
        {
            _pendingCloses.TryRemove(session.Id, out _);
            completion.SetException(new InvalidOperationException("The recovery writer is no longer accepting work."));
        }
        return completion.Task;
    }

    private void UpdateDepth()
    {
        int depth = Interlocked.Increment(ref _depth);
        int maximum = Volatile.Read(ref _maxDepth);
        while (depth > maximum)
        {
            int found = Interlocked.CompareExchange(ref _maxDepth, depth, maximum);
            if (found == maximum) return;
            maximum = found;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var open = new Dictionary<string, OpenState>();
        try
        {
            await foreach (Work work in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _depth);
                if (!open.TryGetValue(work.SessionId, out OpenState? state))
                {
                    string directory = Path.Combine(paths.Recovery, work.SessionId);
                    Directory.CreateDirectory(directory);
                    var stream = new FileStream(Path.Combine(directory, "audio.pcm"), FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, true);
                    state = new(stream, new(work.SessionId, work.Language, work.StartedAt, stream.Length / 2, "recording",
                        work.LogicalTranscriptId, work.PreviousText, work.PriorSegmentCount, work.PriorDurationSeconds, work.LogicalStartedAt));
                    open.Add(work.SessionId, state);
                    await WriteMetadataAsync(directory, state.Metadata, cancellationToken).ConfigureAwait(false);
                }
                if (work.Frame is not null)
                {
                    await state.Stream.WriteAsync(work.Frame.Pcm16, cancellationToken).ConfigureAwait(false);
                    state.Metadata = state.Metadata with { SamplesWritten = state.Metadata.SamplesWritten + work.Frame.Samples.Length };
                }
                if (work.Close || DateTimeOffset.UtcNow - state.LastFlush > TimeSpan.FromSeconds(1))
                {
                    await state.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    state.LastFlush = DateTimeOffset.UtcNow;
                    string directory = Path.Combine(paths.Recovery, work.SessionId);
                    await WriteMetadataAsync(directory, state.Metadata with { State = work.Close ? "finalizing" : "recording" }, cancellationToken).ConfigureAwait(false);
                }
                if (work.Close)
                {
                    await state.Stream.DisposeAsync().ConfigureAwait(false);
                    open.Remove(work.SessionId);
                    _pendingCloses.TryRemove(work.SessionId, out _);
                    work.Completion?.SetResult();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { LogWorkerFailed(logger, ex); }
        finally
        {
            foreach ((string id, OpenState state) in open)
            {
                try { await state.Stream.FlushAsync(CancellationToken.None).ConfigureAwait(false); await state.Stream.DisposeAsync().ConfigureAwait(false); await WriteMetadataAsync(Path.Combine(paths.Recovery, id), state.Metadata, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { LogWriteFailed(logger, ex, id); }
            }
            foreach ((string id, TaskCompletionSource completion) in _pendingCloses)
            {
                if (_pendingCloses.TryRemove(id, out _))
                    completion.TrySetException(new IOException("The recovery writer stopped before the session could be closed."));
            }
        }
    }

    private static Task WriteMetadataAsync(string directory, RecoveryMetadata metadata, CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(Path.Combine(directory, "session.json"), metadata, cancellationToken);

    private static Work CreateWork(DictationSession session, AudioFrame? frame, bool close, TaskCompletionSource? completion = null) =>
        new(session.Id, session.Language, session.StartedAt, session.LogicalStartedAt, session.LogicalTranscriptId, session.PreviousText,
            session.PriorSegmentCount, session.PriorDurationSeconds, frame, close, completion);

    public IEnumerable<string> Discover() => Directory.Exists(paths.Recovery) ? Directory.EnumerateDirectories(paths.Recovery).Where(x => File.Exists(Path.Combine(x, "audio.pcm"))) : [];
    public void Delete(string sessionId) { string directory = Path.Combine(paths.Recovery, sessionId); if (Directory.Exists(directory)) Directory.Delete(directory, true); }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); _queue.Writer.TryComplete();
        if (_activeWorker is not null) try { await _activeWorker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Recovery writer failed while closing session {SessionId}.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, string sessionId);
    [LoggerMessage(Level = LogLevel.Error, Message = "Recovery writer stopped unexpectedly; pending audio remains on disk for recovery.")]
    private static partial void LogWorkerFailed(ILogger logger, Exception exception);
}
