using System.Diagnostics;
using SherpaOnnx;

namespace MetaVoiceType.ConsolePrototype;

public sealed record FinalizeResult(string Text, TimeSpan DecodeTime, double DecodeMsPerAudioSecond);

/// <summary>
/// Owns the sherpa-onnx OnlineRecognizer. One engine serves both live capture
/// sessions and background finalization sessions; sherpa-onnx decodes multiple
/// streams per recognizer, so GPU serialization (if any) is internal and never
/// blocks microphone capture.
/// </summary>
public sealed class TranscriptionEngine : IDisposable
{
    private readonly OnlineRecognizer _recognizer;
    private readonly ILogger _log;

    public TranscriptionEngine(OnlineRecognizerConfig config, ILogger log)
    {
        _recognizer = new OnlineRecognizer(config);
        _log = log;
    }

    public TranscriptionSession CreateSession(string id, string language)
    {
        OnlineStream stream = _recognizer.CreateStream();
        return new TranscriptionSession(id, stream, language, _log);
    }

    /// <summary>
    /// Decodes ready streams and returns their current partial text.
    /// Includes GetResult cost in the session processing time, since modern
    /// sherpa-onnx performs decoding lazily inside GetResult.
    /// </summary>
    public string Process(TranscriptionSession s)
    {
        var sw = Stopwatch.StartNew();
        bool decoded = _recognizer.IsReady(s.Stream);
        if (decoded)
            _recognizer.Decode(s.Stream);
        string text = _recognizer.GetResult(s.Stream).Text;
        sw.Stop();
        s.RecordProcess(sw.Elapsed, decoded);
        return text;
    }

    /// <summary>
    /// Runs the decoder until the stream is drained (IsReady returns false after
    /// InputFinished), returning the final text.
    /// </summary>
    public FinalizeResult FinalizeBlocking(TranscriptionSession session)
    {
        var sw = Stopwatch.StartNew();
        session.MarkInputFinished();
        while (_recognizer.IsReady(session.Stream))
            _recognizer.Decode(session.Stream);
        string text = _recognizer.GetResult(session.Stream).Text;
        sw.Stop();
        var result = new FinalizeResult(text, sw.Elapsed,
            sw.Elapsed.TotalMilliseconds / Math.Max(session.AudioSecondsFed, 0.001));
        _log.LogInformation(
            "Session {Id} finalized in {Ms:F1}ms ({Rate:F2} ms per audio s); text length {Length}.",
            session.Id, sw.Elapsed.TotalMilliseconds, result.DecodeMsPerAudioSecond, text.Length);
        return result;
    }

    public bool IsEndpoint(TranscriptionSession s) => _recognizer.IsEndpoint(s.Stream);
    public void Reset(TranscriptionSession s) => _recognizer.Reset(s.Stream);

    public void Dispose() => _recognizer.Dispose();
}
