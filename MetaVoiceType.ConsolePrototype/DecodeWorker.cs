using System.Threading.Channels;

namespace MetaVoiceType.ConsolePrototype;

public enum DecodeWorkKind
{
    /// <summary>Check for ready live streams (highest priority).</summary>
    LivePoll,

    /// <summary>Drain a finalizing session's tail.</summary>
    Finalize,
}

/// <summary>One unit of decode work for the single decode worker.</summary>
public sealed record DecodeWork(DecodeWorkKind Kind, RecordingSession Session);

/// <summary>
/// Single-threaded decode coordinator for the shared ASR backend.
/// All recognizer Decode/IsReady/GetResult calls happen on this worker's
/// thread, serialized. Live polling has priority over finalization so an old
/// session's drain never delays the active recording.
/// </summary>
public sealed class DecodeWorker : IAsyncDisposable
{
    private readonly Channel<DecodeWork> _queue;
    private readonly Task _loop;
    private readonly CancellationTokenSource _lifetime = new();
    private int _inFlightLive;
    private int _inFlightFinalize;
    private int _queueDepth;
    private long _livePolls;
    private long _finalizeDrains;
    private long _maxObservedQueueDepth;
    private long _lastDecodeMs;

    public int InFlightLive => _inFlightLive;
    public int InFlightFinalize => _inFlightFinalize;
    public long LivePolls => _livePolls;
    public long FinalizeDrains => _finalizeDrains;
    public long MaxObservedQueueDepth => Interlocked.Read(ref _maxObservedQueueDepth);

    /// <summary>Last measured IsReady+Decode+GetResult duration in ms.</summary>
    public double LastDecodeMs => Interlocked.Read(ref _lastDecodeMs) / 1_000_000.0;

    /// <summary>Approximate number of queued work items.</summary>
    public int QueueDepth => Volatile.Read(ref _queueDepth);

    public DecodeWorker()
    {
        _queue = Channel.CreateUnbounded<DecodeWork>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Signal that the live session may have decodeable audio.</summary>
    public void SignalLive(RecordingSession session)
    {
        if (session.State != SessionState.Recording)
            return;
        Interlocked.Increment(ref _inFlightLive);
        if (_queue.Writer.TryWrite(new DecodeWork(DecodeWorkKind.LivePoll, session)))
        {
            IncrementDepth();
            return;
        }
        Interlocked.Decrement(ref _inFlightLive);
    }

    /// <summary>Queue background finalization of a stopped session.</summary>
    public void SignalFinalize(RecordingSession session)
    {
        Interlocked.Increment(ref _inFlightFinalize);
        if (_queue.Writer.TryWrite(new DecodeWork(DecodeWorkKind.Finalize, session)))
        {
            IncrementDepth();
            return;
        }
        Interlocked.Decrement(ref _inFlightFinalize);
    }

    private void IncrementDepth()
    {
        int depth = Interlocked.Increment(ref _queueDepth);
        long max = Interlocked.Read(ref _maxObservedQueueDepth);
        while (depth > max)
        {
            long current = Interlocked.CompareExchange(ref _maxObservedQueueDepth, depth, max);
            if (current == max)
                break; // we wrote it
            max = current; // someone else updated it; retry with fresh value
        }
    }

    private void DecrementDepth()
    {
        Interlocked.Decrement(ref _queueDepth);
    }

    private async Task LoopAsync()
    {
        try
        {
            await foreach (DecodeWork work in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                DecrementDepth();
                try
                {
                    switch (work.Kind)
                    {
                        case DecodeWorkKind.LivePoll:
                            HandleLivePoll(work.Session);
                            break;
                        case DecodeWorkKind.Finalize:
                            HandleFinalize(work.Session);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // A failure in one session must never kill the worker.
                    FaultSession(work.Session, ex);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void HandleLivePoll(RecordingSession session)
    {
        Interlocked.Decrement(ref _inFlightLive);
        if (session.State != SessionState.Recording)
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string text;
        try
        {
            if (!session.StreamReady())
                return;
            text = session.Decode();
        }
        finally
        {
            sw.Stop();
            Interlocked.Exchange(ref _lastDecodeMs, sw.Elapsed.Ticks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency);
        }
        Interlocked.Increment(ref _livePolls);
        session.UpdatePartial(text);
    }

    private void HandleFinalize(RecordingSession session)
    {
        Interlocked.Decrement(ref _inFlightFinalize);
        if (session.State != SessionState.Finalizing)
            return;

        session.MarkFinalizeStarted();
        try
        {
            int drains = 0;
            while (session.StreamReady())
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                session.Decode();
                sw.Stop();
                Interlocked.Exchange(ref _lastDecodeMs, sw.Elapsed.Ticks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency);
                Interlocked.Increment(ref _finalizeDrains);
                if (++drains > 10000)
                {
                    // Defensive cap against a misbehaving native stream.
                    throw new InvalidOperationException(
                        $"Finalize drain exceeded 10000 iterations for session {session.Id}.");
                }
            }
            string final = session.GetResultText();
            session.UpdatePartial(final);
            session.Complete(final);
        }
        catch (Exception ex)
        {
            FaultSession(session, ex);
        }
        finally
        {
            session.Dispose();
        }
    }

    private void FaultSession(RecordingSession session, Exception ex)
    {
        if (session.State is SessionState.Recording or SessionState.Finalizing)
        {
            session.Fail(ex);
            session.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { /* worker cancelled */ }
        _lifetime.Dispose();
    }

    private volatile bool _disposed;
}
