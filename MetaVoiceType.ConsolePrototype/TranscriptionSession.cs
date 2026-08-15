using System.Diagnostics;
using SherpaOnnx;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Wraps a single sherpa-onnx OnlineStream with per-stream language option,
/// partial-result tracking, and timing instrumentation.
/// </summary>
public sealed class TranscriptionSession : IDisposable
{
    private readonly OnlineStream _stream;
    private readonly ILogger _log;
    private int _decodeCalls;
    private long _processNs;
    private long _samplesFed;
    private string _lastPartial = string.Empty;

    public string Id { get; }
    public OnlineStream Stream => _stream;
    public double AudioSecondsFed => _samplesFed / 16000.0;
    public int DecodeCalls => _decodeCalls;

    public TranscriptionSession(string id, OnlineStream stream, string language, ILogger log)
    {
        Id = id;
        _stream = stream;
        _log = log;
        stream.SetOption("language", language);
    }

    public void Feed(float[] samples, int sampleRate)
    {
        _stream.AcceptWaveform(sampleRate, samples);
        _samplesFed += samples.Length * 16000 / sampleRate;
    }

    public void MarkInputFinished() => _stream.InputFinished();

    /// <summary>Accumulate wall-clock time spent in Decode + GetResult.</summary>
    public void RecordProcess(TimeSpan elapsed, bool decoded)
    {
        if (decoded) _decodeCalls++;
        _processNs += (long)elapsed.TotalNanoseconds;
    }

    public bool HasNewPartial(string current) => current != _lastPartial;

    public void CommitPartial(string partial) => _lastPartial = partial;

    /// <summary>Processing cost in ms per second of audio fed.</summary>
    public double GetProcessMsPerAudioSecond()
    {
        double audioSeconds = Math.Max(AudioSecondsFed, 0.001);
        return _processNs / 1_000_000.0 / audioSeconds;
    }

    public void Dispose() => _stream.Dispose();
}
