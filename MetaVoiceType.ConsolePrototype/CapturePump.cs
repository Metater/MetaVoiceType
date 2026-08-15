using System.Diagnostics;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Feeds audio frames from a source into a recording session. In production
/// one pump (bound to the coordinator's active slot) runs for the whole app
/// lifetime; tests bind pumps to specific sessions. Frames are consumed and
/// each feed is followed by a decode signal — never an inline decode.
/// </summary>
public sealed class CapturePump
{
    private readonly IAudioSource _source;
    private readonly SessionCoordinator _coordinator;
    private readonly RecordingSession? _boundSession;
    private readonly Action<RecordingSession, float[]>? _onFrame;

    public long FramesPumped { get; private set; }
    public long MaxLagMs { get; private set; }
    public long MaxPollSignalGapMs { get; private set; }

    public CapturePump(IAudioSource source, SessionCoordinator coordinator,
        RecordingSession? boundSession = null,
        Action<RecordingSession, float[]>? onFrame = null)
    {
        _source = source;
        _coordinator = coordinator;
        _boundSession = boundSession;
        _onFrame = onFrame;
    }

    /// <summary>
    /// Runs until the source ends or cancellation. Frames arriving while the
    /// target session is not Recording are counted and skipped.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long lastSignalMs = 0;

        try
        {
            await foreach (float[] frame in _source.ReadFramesAsync(ct).ConfigureAwait(false))
            {
                RecordingSession? session = _boundSession ?? _coordinator.Active;
                if (session is { IsRecording: true })
                {
                    session.Feed(frame, _source.SampleRate);
                    _coordinator.SignalLive(session);
                    _onFrame?.Invoke(session, frame);

                    long wallMs = sw.ElapsedMilliseconds;
                    long lagMs = wallMs - (long)(session.AudioSecondsFed * 1000);
                    if (lagMs > MaxLagMs)
                        MaxLagMs = lagMs;
                    long gap = wallMs - lastSignalMs;
                    if (gap > MaxPollSignalGapMs && lastSignalMs > 0)
                        MaxPollSignalGapMs = gap;
                    lastSignalMs = wallMs;
                }
                FramesPumped++;
            }
        }
        finally
        {
            sw.Stop();
        }
    }
}
