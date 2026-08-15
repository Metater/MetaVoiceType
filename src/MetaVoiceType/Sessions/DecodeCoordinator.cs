using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.Sessions;

public sealed partial class DecodeCoordinator(ILogger<DecodeCoordinator> logger) : IAsyncDisposable
{
    private sealed record Work(DictationSession Session, DictationSegment Segment);
    private readonly Channel<Work> _jobs = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;
    private int _depth;
    public int QueueDepth => Volatile.Read(ref _depth);
    public event EventHandler<DictationSession>? TranscriptChanged;
    public event EventHandler<DictationSession>? SessionCompleted;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_lifetime.Token));

    public void Enqueue(DictationSession session, IEnumerable<DictationSegment> segments)
    {
        foreach (DictationSegment segment in segments)
        {
            if (_jobs.Writer.TryWrite(new(session, segment))) Interlocked.Increment(ref _depth);
        }
    }

    public void Finalize(DictationSession session, IEnumerable<DictationSegment> tailSegments)
    {
        Enqueue(session, tailSegments);
        if (session.TryCompleteWithoutPending()) SessionCompleted?.Invoke(this, session);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Work work in _jobs.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _depth);
                try
                {
                    IReadOnlyList<float[]> slices = work.Session.GetDecodeSlices(work.Segment);
                    string text = string.Join(' ', slices.Select(work.Session.TranscribeForCoordinator).Where(x => !string.IsNullOrWhiteSpace(x)));
                    SegmentCompletion completion = work.Session.CompleteSegment(work.Segment, text);
                    if (completion is SegmentCompletion.TranscriptChanged or SegmentCompletion.SessionCompleted)
                        TranscriptChanged?.Invoke(this, work.Session);
                    if (completion == SegmentCompletion.SessionCompleted) SessionCompleted?.Invoke(this, work.Session);
                }
                catch (Exception ex)
                {
                    work.Session.Fault();
                    LogSessionFault(logger, ex, work.Session.Id);
                    SessionCompleted?.Invoke(this, work.Session);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _jobs.Writer.TryComplete();
        if (_loop is not null) try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Session {SessionId} failed during Parakeet segment decoding.")]
    private static partial void LogSessionFault(ILogger logger, Exception exception, string sessionId);
}
