namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Coordinates the recording lifecycle: at most one session accepts new audio
/// at a time; stopping a session frees the slot immediately and enqueues
/// background finalization. There is no global busy state.
/// </summary>
public sealed class SessionCoordinator
{
    private readonly IAsrBackend _backend;
    private readonly DecodeWorker _worker;
    private readonly object _gate = new();
    private RecordingSession? _active;
    private readonly List<RecordingSession> _all = new();
    private int _sequence;

    /// <summary>Session currently accepting capture audio (null when idle).</summary>
    public RecordingSession? Active
    {
        get { lock (_gate) return _active; }
    }

    public int FinalizingCount => _worker.InFlightFinalize;

    public SessionCoordinator(IAsrBackend backend, DecodeWorker worker)
    {
        _backend = backend;
        _worker = worker;
    }

    /// <summary>
    /// Starts a new recording session. Returns null if a recording is already
    /// active (callers should surface the rejection, not block).
    /// </summary>
    public RecordingSession? TryStart(string language)
    {
        lock (_gate)
        {
            if (_active is { IsRecording: true })
                return null;

            string id = $"S{Interlocked.Increment(ref _sequence):D4}";
            var session = new RecordingSession(id, language, _backend.CreateStream(language));
            _active = session;
            _all.Add(session);
            return session;
        }
    }

    /// <summary>
    /// Stops the active recording and queues its background finalization.
    /// The active slot is freed before this returns. If the session already
    /// left Recording (e.g. a worker faulted it), it is simply detached.
    /// </summary>
    public RecordingSession? StopActive()
    {
        RecordingSession? session;
        lock (_gate)
        {
            session = _active;
            _active = null;
        }
        if (session is null)
            return null;
        if (session.IsRecording)
        {
            session.Stop();
            _worker.SignalFinalize(session);
        }
        return session;
    }

    /// <summary>Queues a live decode poll for the given session.</summary>
    public void SignalLive(RecordingSession session) => _worker.SignalLive(session);

    public IReadOnlyList<RecordingSession> All => _all;
}
