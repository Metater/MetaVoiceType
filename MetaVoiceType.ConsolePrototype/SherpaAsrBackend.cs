using System.Diagnostics;
using SherpaOnnx;

namespace MetaVoiceType.ConsolePrototype;

public sealed record FinalizeResult(string Text, TimeSpan DecodeTime, double DecodeMsPerAudioSecond);

/// <summary>
/// sherpa-onnx OnlineRecognizer adapter. One recognizer is shared by all
/// sessions (one OnlineStream per recording). Decode/IsReady/GetResult are
/// routed through the managed OnlineRecognizer API and are always called from
/// the DecodeWorker thread; AcceptWaveform is called from the capture
/// pipeline, matching sherpa's own microphone examples.
/// </summary>
public sealed class SherpaAsrBackend : IAsrBackend
{
    private readonly OnlineRecognizer _recognizer;

    public SherpaAsrBackend(OnlineRecognizerConfig config, ILogger log)
    {
        var sw = Stopwatch.StartNew();
        _recognizer = new OnlineRecognizer(config);
        sw.Stop();
        log.LogInformation("sherpa recognizer created in {Ms:F1}ms (sherpa-onnx {Sherpa}, onnxruntime {Ort}).",
            sw.Elapsed.TotalMilliseconds, SherpaOnnx.VersionInfo.Version, SherpaOnnx.VersionInfo.OnnxruntimeVersion);
    }

    public IAsrStream CreateStream(string language) =>
        new Stream(this, _recognizer.CreateStream(), language);

    public void Dispose() => _recognizer.Dispose();

    private sealed class Stream : IAsrStream
    {
        private readonly SherpaAsrBackend _backend;
        private readonly OnlineStream _stream;
        private long _samplesFed;
        private int _disposed;

        public Stream(SherpaAsrBackend backend, OnlineStream stream, string language)
        {
            _backend = backend;
            _stream = stream;
            _stream.SetOption("language", language);
        }

        public void Feed(float[] samples, int sampleRate)
        {
            _stream.AcceptWaveform(sampleRate, samples);
            _samplesFed += samples.Length * 16000L / sampleRate;
        }

        public void MarkInputFinished() => _stream.InputFinished();

        public bool IsReady() => _backend._recognizer.IsReady(_stream);

        public string Decode()
        {
            _backend._recognizer.Decode(_stream);
            return _backend._recognizer.GetResult(_stream).Text;
        }

        public string GetResultText() => _backend._recognizer.GetResult(_stream).Text;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _stream.Dispose();
        }
    }
}
