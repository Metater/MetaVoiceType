using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.Sessions;

public sealed partial class DecodeCoordinator(ILogger<DecodeCoordinator> logger) : IAsyncDisposable
{
    private readonly Channel<DictationSession> _live = Channel.CreateUnbounded<DictationSession>();
    private readonly Channel<DictationSession> _final = Channel.CreateUnbounded<DictationSession>();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;
    private int _liveDepth;
    private int _finalDepth;
    public int LiveQueueDepth => Volatile.Read(ref _liveDepth);
    public int FinalizationQueueDepth => Volatile.Read(ref _finalDepth);
    public event EventHandler<DictationSession>? TranscriptChanged;
    public event EventHandler<DictationSession>? SessionCompleted;

    public void Start() => _loop ??= Task.Run(() => RunAsync(_lifetime.Token));
    public void SignalLive(DictationSession session) { if (_live.Writer.TryWrite(session)) Interlocked.Increment(ref _liveDepth); }
    public void Finalize(DictationSession session) { if (_final.Writer.TryWrite(session)) Interlocked.Increment(ref _finalDepth); }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_live.Reader.TryRead(out DictationSession? live))
            {
                Interlocked.Decrement(ref _liveDepth);
                if (live.Status == Core.Models.DictationStatus.Recording && live.Ready)
                {
                    TryDecode(live);
                    TranscriptChanged?.Invoke(this, live);
                }
            }

            if (_final.Reader.TryRead(out DictationSession? final))
            {
                Interlocked.Decrement(ref _finalDepth);
                try
                {
                    int guard = 0;
                    while (final.Ready)
                    {
                        TryDecode(final);
                        DrainLiveQueue();
                        if (++guard > 10000) throw new InvalidOperationException("ASR finalization exceeded its decode guard.");
                    }
                    final.Complete(final.CurrentResult);
                    SessionCompleted?.Invoke(this, final);
                }
                catch (Exception ex) { final.Fault(); LogSessionFault(logger, ex, final.Id); SessionCompleted?.Invoke(this, final); }
                finally { final.Dispose(); }
                continue;
            }

            Task liveReady = _live.Reader.WaitToReadAsync(cancellationToken).AsTask();
            Task finalReady = _final.Reader.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(liveReady, finalReady).ConfigureAwait(false);
        }
    }

    private static void TryDecode(DictationSession session) => session.Decode();

    private void DrainLiveQueue()
    {
        while (_live.Reader.TryRead(out DictationSession? session))
        {
            Interlocked.Decrement(ref _liveDepth);
            if (session.Status == Core.Models.DictationStatus.Recording && session.Ready)
            {
                TryDecode(session);
                TranscriptChanged?.Invoke(this, session);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _live.Writer.TryComplete(); _final.Writer.TryComplete();
        if (_loop is not null) try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Session {SessionId} failed during ASR decode.")]
    private static partial void LogSessionFault(ILogger logger, Exception exception, string sessionId);
}
