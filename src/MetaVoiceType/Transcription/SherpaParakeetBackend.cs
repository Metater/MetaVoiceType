using MetaVoiceType.Models;
using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace MetaVoiceType.Transcription;

public sealed partial class SherpaParakeetBackend : IAsrBackend
{
    private readonly object _decodeGate = new();
    private readonly OfflineRecognizer _recognizer;

    public SherpaParakeetBackend(string modelDirectory, ModelArtifact model, SherpaRuntimeBootstrapper runtime,
        ILogger<SherpaParakeetBackend> logger)
    {
        if (model.Kind != ModelArtifactKinds.Dictation) throw new ArgumentException("A dictation artifact is required.", nameof(model));
        bool cudaRuntime = runtime.Configure();
        string requestedProvider = cudaRuntime ? "cuda" : "cpu";
        string? fallbackReason = runtime.RuntimeFailure;
        try
        {
            _recognizer = Create(modelDirectory, model, requestedProvider);
            WarmUp();
        }
        catch (Exception cudaFailure) when (requestedProvider == "cuda")
        {
            fallbackReason = $"CUDA initialization failed: {cudaFailure.Message}";
            LogCudaFailed(logger, cudaFailure);
            _recognizer = Create(modelDirectory, model, "cpu");
            requestedProvider = "cpu";
            WarmUp();
        }

        Status = new(model.Id, ShortName(model.Id), requestedProvider, requestedProvider == "cuda" ? "GPU" : "CPU",
            requestedProvider == "cuda" ? runtime.GpuName : null, VersionInfo.Version, fallbackReason);
        LogInitialized(logger, model.Id, requestedProvider, VersionInfo.Version);
    }

    public AsrRuntimeStatus Status { get; }

    public string Transcribe(float[] samples)
    {
        if (samples.Length == 0) return "";
        lock (_decodeGate)
        {
            using OfflineStream stream = _recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples);
            _recognizer.Decode(stream);
            return stream.Result.Text.Trim();
        }
    }

    private static OfflineRecognizer Create(string directory, ModelArtifact model, string provider)
    {
        string Resolve(string path) => Path.Combine(directory, path);
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Resolve(model.Files.Encoder!);
        config.ModelConfig.Transducer.Decoder = Resolve(model.Files.Decoder!);
        config.ModelConfig.Transducer.Joiner = Resolve(model.Files.Joiner!);
        config.ModelConfig.Tokens = Resolve(model.Files.Tokens!);
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.Provider = provider;
        config.ModelConfig.NumThreads = provider == "cuda" ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        config.MaxActivePaths = 4;
        return new OfflineRecognizer(config);
    }

    private void WarmUp() => Transcribe(new float[1600]);
    private static string ShortName(string id) => id == "parakeet-v2" ? "Parakeet v2" : "Parakeet v3";
    public void Dispose() => _recognizer.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "{Model} initialized with provider={Provider} (sherpa {SherpaVersion}).")]
    private static partial void LogInitialized(ILogger logger, string model, string provider, string sherpaVersion);
    [LoggerMessage(Level = LogLevel.Warning, Message = "CUDA initialization failed; retrying the same Parakeet model on CPU.")]
    private static partial void LogCudaFailed(ILogger logger, Exception exception);
}
