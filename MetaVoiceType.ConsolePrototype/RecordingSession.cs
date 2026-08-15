using System.Diagnostics;

namespace MetaVoiceType.ConsolePrototype;

public enum SessionState
{
    /// <summary>Created; active capture target. Accepts audio.</summary>
    Recording,

    /// <summary>Detached from capture; awaiting final drain on the decode worker.</summary>
    Finalizing,

    /// <summary>Final transcript available; stream disposed.</summary>
    Completed,

    /// <summary>Finalization failed; stream disposed.</summary>
    Faulted,
}

/// <summary>
/// Per-recording session. Owns its ASR stream and all session-scoped
/// diagnostics. Audio feeding is capture-side; decode/finalize run on the
/// decode worker.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    private readonly IAsrStream _stream;
    private readonly object _gate = new();
    private readonly Stopwatch _decodeClock = Stopwatch.StartNew();
    private long _processNs;
    private long _decodeCalls;
    private long _samplesFed;
    private SessionState _state;
    private string _partial = string.Empty;
    private string _final = string.Empty;
    private Exception? _fault;

    public string Id { get; }
    public string Language { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? StoppedAt { get; private set; }
    public DateTimeOffset? FinalizeQueuedAt { get; private set; }
    public DateTimeOffset? FinalizeStartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Milliseconds between Stop and the finalize completion.</summary>
    public double? FinalizationLatencyMs { get; private set; }

    public SessionState State => _state;
    public Exception? Fault => _fault;
    public bool IsRecording => _state == SessionState.Recording;
    public bool IsFinalizing => _state == SessionState.Finalizing;

    /// <summary>Total processing (decode+result) ms per audio second fed.</summary>
    public double ProcessMsPerAudioSecond =>
        _processNs / 1_000_000.0 / Math.Max(AudioSecondsFed, 0.001);

    public double AudioSecondsFed => _samplesFed / 16000.0;
    public long DecodeCalls => _decodeCalls;
    public long SamplesFed => _samplesFed;

    public string PartialTranscript
    {
        get { lock (_gate) return _partial; }
    }

    public string FinalTranscript
    {
        get { lock (_gate) return _final; }
    }

    public RecordingSession(string id, string language, IAsrStream stream)
    {
        Id = id;
        Language = language;
        _stream = stream;
        _state = SessionState.Recording;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Called by the capture pipeline. Never runs inference.</summary>
    public void Feed(float[] samples, int sampleRate)
    {
        if (_state != SessionState.Recording)
            throw new InvalidOperationException(
                $"Session {Id} is {_state}; audio can only be fed while Recording.");
        _stream.Feed(samples, sampleRate);
        _samplesFed += samples.Length * 16000L / sampleRate;
    }

    /// <summary>ASR surface called only by the decode worker.</summary>
    public bool StreamReady() => _stream.IsReady();

    public string Decode()
    {
        var sw = Stopwatch.StartNew();
        string text = _stream.Decode();
        sw.Stop();
        _processNs += (long)sw.Elapsed.TotalNanoseconds;
        _decodeCalls++;
        return text;
    }

    public string GetResultText() => _stream.GetResultText();

    public void UpdatePartial(string text)
    {
        lock (_gate) _partial = text;
    }

    /// <summary>
    /// Detach from capture and mark for background finalization.
    /// Returns immediately; the caller may start a new recording right away.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_state != SessionState.Recording)
                throw new InvalidOperationException($"Session {Id} is {_state}, not Recording.");
            _state = SessionState.Finalizing;
            StoppedAt = DateTimeOffset.UtcNow;
            FinalizeQueuedAt = DateTimeOffset.UtcNow;
        }
        _stream.MarkInputFinished();
    }

    public void MarkFinalizeStarted()
    {
        lock (_gate)
        {
            FinalizeStartedAt ??= DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Called by the decode worker when the stream is drained.</summary>
    public void Complete(string finalText)
    {
        lock (_gate)
        {
            _state = SessionState.Completed;
            _final = finalText;
            CompletedAt = DateTimeOffset.UtcNow;
            if (FinalizeQueuedAt is { } queued)
                FinalizationLatencyMs = (CompletedAt.Value - queued).TotalMilliseconds;
        }
    }

    public void Fail(Exception ex)
    {
        lock (_gate)
        {
            _state = SessionState.Faulted;
            _fault = ex;
            CompletedAt = DateTimeOffset.UtcNow;
            if (FinalizeQueuedAt is { } queued)
                FinalizationLatencyMs = (CompletedAt.Value - queued).TotalMilliseconds;
        }
    }

    public void Dispose() => _stream.Dispose();
}
