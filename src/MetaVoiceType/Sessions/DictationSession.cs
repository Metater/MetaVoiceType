using System.Diagnostics;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Transcription;

namespace MetaVoiceType.Sessions;

public sealed class DictationSession : IDisposable
{
    private readonly object _gate = new();
    private readonly IAsrChannel _stream;
    private string _liveText = "";
    private string _finalText = "";
    private DictationStatus _status = DictationStatus.Recording;
    private int _disposed;

    public DictationSession(string language, IAsrChannel stream, string? id = null, DateTimeOffset? startedAt = null)
    {
        Id = id ?? Guid.NewGuid().ToString("N");
        Language = language;
        _stream = stream;
        StartedAt = startedAt ?? DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public string Language { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? StoppedAt { get; private set; }
    public bool Canceled { get; private set; }
    public bool PasteRequested { get; private set; }
    public bool Copied { get; set; }
    public bool Pasted { get; set; }
    public long SamplesAccepted { get; private set; }
    public double? FinalizationMilliseconds { get; private set; }
    public Stopwatch FinalizationClock { get; } = new();
    public DictationStatus Status { get { lock (_gate) return _status; } }
    public string LiveText { get { lock (_gate) return _liveText; } }
    public string FinalText { get { lock (_gate) return _finalText; } }

    public void Accept(Audio.AudioFrame frame)
    {
        lock (_gate)
        {
            if (_status != DictationStatus.Recording) return;
            _stream.Accept(frame.Samples);
            SamplesAccepted += frame.Samples.Length;
        }
    }

    public void Stop(bool canceled, bool pasteRequested)
    {
        lock (_gate)
        {
            if (_status != DictationStatus.Recording) return;
            Canceled = canceled;
            PasteRequested = pasteRequested;
            StoppedAt = DateTimeOffset.UtcNow;
            _status = DictationStatus.Finalizing;
            FinalizationClock.Restart();
            _stream.Finish();
        }
    }

    public bool Ready => _stream.IsReady();
    public void Decode() { string text = _stream.Decode(); lock (_gate) _liveText = text; }
    public void Complete(string text)
    {
        lock (_gate)
        {
            _finalText = text;
            _liveText = text;
            _status = Canceled ? DictationStatus.Canceled : DictationStatus.Completed;
            FinalizationClock.Stop();
            FinalizationMilliseconds = FinalizationClock.Elapsed.TotalMilliseconds;
        }
    }
    public void Fault() { lock (_gate) _status = DictationStatus.Faulted; }
    public string CurrentResult => _stream.CurrentText;
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _stream.Dispose(); }
}
