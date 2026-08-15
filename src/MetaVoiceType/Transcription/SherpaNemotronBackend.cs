using MetaVoiceType.Models;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace MetaVoiceType.Transcription;

public sealed partial class SherpaNemotronBackend : IAsrBackend
{
    private readonly OnlineRecognizer _recognizer;
    public string Acceleration => "CPU";

    public SherpaNemotronBackend(string modelDirectory, DictationModel model, ILogger<SherpaNemotronBackend> logger)
    {
        string Resolve(string file) => Path.Combine(modelDirectory, file);
        var config = new OnlineRecognizerConfig
        {
            FeatConfig = { SampleRate = 16000, FeatureDim = 80 },
            ModelConfig =
            {
                Transducer =
                {
                    Encoder = Resolve(model.Files.Encoder), Decoder = Resolve(model.Files.Decoder), Joiner = Resolve(model.Files.Joiner)
                },
                Tokens = Resolve(model.Files.Tokens),
                Provider = "cpu",
                NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
                Debug = 0
            },
            DecodingMethod = "greedy_search",
            MaxActivePaths = 4,
            EnableEndpoint = 0,
            Rule1MinTrailingSilence = 2.4f,
            Rule2MinTrailingSilence = 1.2f,
            Rule3MinUtteranceLength = 300f
        };
        _recognizer = new OnlineRecognizer(config);
        LogInitialized(logger, VersionInfo.Version);
    }

    public IAsrChannel CreateStream(string language) => new Stream(_recognizer, language);
    public void Dispose() => _recognizer.Dispose();

    private sealed class Stream(OnlineRecognizer recognizer, string language) : IAsrChannel
    {
        private readonly OnlineStream _stream = Create(recognizer, language);
        public void Accept(float[] samples) => _stream.AcceptWaveform(16000, samples);
        public void Finish() => _stream.InputFinished();
        public bool IsReady() => recognizer.IsReady(_stream);
        public string Decode() { recognizer.Decode(_stream); return CurrentText; }
        public string CurrentText => recognizer.GetResult(_stream).Text;
        public void Dispose() => _stream.Dispose();
        private static OnlineStream Create(OnlineRecognizer owner, string value)
        {
            OnlineStream stream = owner.CreateStream();
            stream.SetOption("language", value);
            return stream;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Nemotron recognizer initialized on CPU (sherpa {SherpaVersion}).")]
    private static partial void LogInitialized(ILogger logger, string sherpaVersion);
}
