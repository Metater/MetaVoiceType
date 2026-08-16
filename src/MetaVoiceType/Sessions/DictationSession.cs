using System.Diagnostics;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Transcription;

namespace MetaVoiceType.Sessions;

public sealed record ControlAudioSpan(long StartSample, long EndSample)
{
    public bool IsValid => EndSample > StartSample;
    public bool Overlaps(long start, long end) => StartSample < end && EndSample > start;
}

public sealed record DictationSegment(Guid Id, long StartSample, long EndSample, int Revision);
public enum SegmentCompletion { Stale, TranscriptChanged, SessionCompleted }

public sealed class DictationSession : IDisposable
{
    private sealed class SegmentState(Guid id, long start, float[] samples)
    {
        public Guid Id { get; } = id;
        public long Start { get; } = start;
        public long End => Start + Samples.LongLength;
        public float[] Samples { get; } = samples;
        public int Revision { get; set; }
        public string Text { get; set; } = "";
    }

    private readonly object _gate = new();
    private readonly ISpeechSegmenter _vad;
    private readonly IAsrBackend _backend;
    private readonly Dictionary<Guid, SegmentState> _segments = [];
    private readonly List<ControlAudioSpan> _controlSpans = [];
    private DictationStatus _status = DictationStatus.Recording;
    private int _pendingJobs;
    private bool _completionPublished;
    private int _disposed;
    private readonly TranscriptRecord? _continuedRecord;

    public DictationSession(string language, long globalStartSample, IAsrBackend backend, string vadModelPath,
        string? id = null, DateTimeOffset? startedAt = null, TranscriptRecord? continuedRecord = null)
        : this(language, globalStartSample, backend, new SherpaVadSegmenter(vadModelPath), id, startedAt, continuedRecord)
    {
    }

    public DictationSession(string language, long globalStartSample, IAsrBackend backend, ISpeechSegmenter segmenter,
        string? id = null, DateTimeOffset? startedAt = null, TranscriptRecord? continuedRecord = null)
    {
        Id = id ?? Guid.NewGuid().ToString("N");
        Language = language;
        GlobalStartSample = globalStartSample;
        _backend = backend;
        _vad = segmenter;
        StartedAt = startedAt ?? DateTimeOffset.UtcNow;
        _continuedRecord = continuedRecord;
    }

    public string Id { get; }
    public string Language { get; }
    public long GlobalStartSample { get; }
    public DateTimeOffset StartedAt { get; }
    public string LogicalTranscriptId => _continuedRecord?.LogicalId ?? Id;
    public DateTimeOffset LogicalStartedAt => _continuedRecord?.StartedAt ?? StartedAt;
    public int PriorSegmentCount => _continuedRecord?.SegmentCount ?? 0;
    public double PriorDurationSeconds => _continuedRecord?.TotalDurationSeconds ?? 0;
    public bool IsContinuation => _continuedRecord is not null;
    public string PreviousText => _continuedRecord?.Text ?? "";
    public DateTimeOffset? StoppedAt { get; private set; }
    public bool Canceled { get; private set; }
    public bool PasteRequested { get; private set; }
    public bool Copied { get; set; }
    public bool Pasted { get; set; }
    public long SamplesAccepted { get; private set; }
    public double? FinalizationMilliseconds { get; private set; }
    public Stopwatch FinalizationClock { get; } = new();
    public AsrRuntimeStatus RuntimeStatus => _backend.Status;
    internal IAsrBackend Backend => _backend;
    public DictationStatus Status { get { lock (_gate) return _status; } }
    public string SegmentText { get { lock (_gate) return AssembleText(); } }
    public string LiveText { get { lock (_gate) return Combine(_continuedRecord?.Text, AssembleText()); } }
    public string FinalText { get { lock (_gate) return Combine(_continuedRecord?.Text, AssembleText()); } }
    public IReadOnlyList<ControlAudioSpan> ControlSpans { get { lock (_gate) return _controlSpans.ToArray(); } }

    public IReadOnlyList<DictationSegment> Accept(Audio.AudioFrame frame)
    {
        lock (_gate)
        {
            if (_status != DictationStatus.Recording) return [];
            SamplesAccepted += frame.Samples.Length;
            return AddSegments(_vad.Accept(frame.Samples));
        }
    }

    public IReadOnlyList<DictationSegment> Stop(bool canceled, bool pasteRequested)
    {
        lock (_gate)
        {
            if (_status != DictationStatus.Recording) return [];
            Canceled = canceled;
            PasteRequested |= pasteRequested;
            StoppedAt = DateTimeOffset.UtcNow;
            _status = DictationStatus.Finalizing;
            FinalizationClock.Restart();
            return AddSegments(_vad.Flush());
        }
    }

    public bool RequestPaste()
    {
        lock (_gate)
        {
            if (_status is not (DictationStatus.Recording or DictationStatus.Finalizing or DictationStatus.Completed)) return false;
            if (PasteRequested) return false;
            PasteRequested = true;
            return true;
        }
    }

    public IReadOnlyList<DictationSegment> MarkControlSpan(long globalStartSample, long globalEndSample)
    {
        long start = Math.Max(0, globalStartSample - GlobalStartSample);
        long end = Math.Min(SamplesAccepted, globalEndSample - GlobalStartSample);
        var span = new ControlAudioSpan(start, end);
        if (!span.IsValid) return [];
        lock (_gate)
        {
            _controlSpans.Add(span);
            var jobs = new List<DictationSegment>();
            foreach (SegmentState segment in _segments.Values.Where(x => span.Overlaps(x.Start, x.End)))
            {
                segment.Revision++;
                segment.Text = "";
                _pendingJobs++;
                jobs.Add(new(segment.Id, segment.Start, segment.End, segment.Revision));
            }
            return jobs;
        }
    }

    public IReadOnlyList<float[]> GetDecodeSlices(DictationSegment job)
    {
        lock (_gate)
        {
            if (!_segments.TryGetValue(job.Id, out SegmentState? segment) || segment.Revision != job.Revision) return [];
            var retained = new List<(long Start, long End)> { (segment.Start, segment.End) };
            foreach (ControlAudioSpan control in _controlSpans.Where(x => x.Overlaps(segment.Start, segment.End)).OrderBy(x => x.StartSample))
            {
                var next = new List<(long Start, long End)>();
                foreach ((long start, long end) in retained)
                {
                    if (!control.Overlaps(start, end)) { next.Add((start, end)); continue; }
                    if (control.StartSample > start) next.Add((start, Math.Min(control.StartSample, end)));
                    if (control.EndSample < end) next.Add((Math.Max(control.EndSample, start), end));
                }
                retained = next;
            }
            return retained.Where(x => x.End - x.Start >= 800)
                .Select(x => segment.Samples.AsSpan(checked((int)(x.Start - segment.Start)), checked((int)(x.End - x.Start))).ToArray()).ToArray();
        }
    }

    public SegmentCompletion CompleteSegment(DictationSegment job, string text)
    {
        lock (_gate)
        {
            _pendingJobs = Math.Max(0, _pendingJobs - 1);
            bool changed = false;
            if (_segments.TryGetValue(job.Id, out SegmentState? segment) && segment.Revision == job.Revision)
            {
                segment.Text = text.Trim();
                changed = true;
            }
            if (TryCompleteCore()) return SegmentCompletion.SessionCompleted;
            return changed ? SegmentCompletion.TranscriptChanged : SegmentCompletion.Stale;
        }
    }

    public bool TryCompleteWithoutPending() { lock (_gate) return TryCompleteCore(); }

    public void Fault()
    {
        lock (_gate)
        {
            _status = DictationStatus.Faulted;
            FinalizationClock.Stop();
            FinalizationMilliseconds = FinalizationClock.Elapsed.TotalMilliseconds;
        }
    }

    private List<DictationSegment> AddSegments(IReadOnlyList<SpeechAudioSegment> segments)
    {
        var jobs = new List<DictationSegment>(segments.Count);
        foreach (SpeechAudioSegment speech in segments)
        {
            var state = new SegmentState(Guid.NewGuid(), speech.StartSample, speech.Samples);
            _segments.Add(state.Id, state);
            _pendingJobs++;
            jobs.Add(new(state.Id, state.Start, state.End, state.Revision));
        }
        return jobs;
    }

    private bool TryCompleteCore()
    {
        if (_completionPublished || _status != DictationStatus.Finalizing || _pendingJobs != 0) return false;
        _completionPublished = true;
        _status = Canceled ? DictationStatus.Canceled : DictationStatus.Completed;
        FinalizationClock.Stop();
        FinalizationMilliseconds = FinalizationClock.Elapsed.TotalMilliseconds;
        return true;
    }

    private string AssembleText() => string.Join(' ', _segments.Values.OrderBy(x => x.Start).Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    private static string Combine(string? previous, string? appended) => string.Join(' ', new[] { previous, appended }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    internal string TranscribeForCoordinator(float[] samples) => _backend.Transcribe(samples);
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _vad.Dispose(); }
}
