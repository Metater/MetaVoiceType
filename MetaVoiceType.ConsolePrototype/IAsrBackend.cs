namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// A handle to a live ASR stream. Implementations wrap the sherpa-onnx
/// OnlineStream (production) or a fake (unit tests).
/// </summary>
public interface IAsrStream : IDisposable
{
    /// <summary>Feed raw mono audio samples at the given sample rate.</summary>
    void Feed(float[] samples, int sampleRate);

    /// <summary>Signal end-of-input so the decoder can drain the tail.</summary>
    void MarkInputFinished();

    /// <summary>True when the stream has decodeable audio pending.</summary>
    bool IsReady();

    /// <summary>Run one decode step. Returns the current partial text.</summary>
    string Decode();

    /// <summary>Current result text without decoding.</summary>
    string GetResultText();
}

/// <summary>
/// Owns the shared ASR model (one sherpa OnlineRecognizer in production).
/// All ASR calls must be made through one thread at a time.
/// </summary>
public interface IAsrBackend : IDisposable
{
    IAsrStream CreateStream(string language);
}
