using CommandLine;

namespace MetaVoiceType.ConsolePrototype;

public class Options
{
    [Option("list-devices", Required = false, Default = false,
        HelpText = "List PortAudio input devices and exit.")]
    public bool ListDevices { get; set; }

    [Option("encoder", Required = false, HelpText = "Path to transducer encoder.onnx.")]
    public string Encoder { get; set; } = string.Empty;

    [Option("decoder", Required = false, HelpText = "Path to transducer decoder.onnx.")]
    public string Decoder { get; set; } = string.Empty;

    [Option("joiner", Required = false, HelpText = "Path to transducer joiner.onnx.")]
    public string Joiner { get; set; } = string.Empty;

    [Option("tokens", Required = false, HelpText = "Path to tokens.txt.")]
    public string Tokens { get; set; } = string.Empty;

    [Option("provider", Required = false, Default = "cpu",
        HelpText = "ONNX execution provider: cpu | cuda")]
    public string Provider { get; set; } = "cpu";

    [Option("device", Required = false, Default = 0, HelpText = "CUDA device index.")]
    public int Device { get; set; }

    [Option("language", Required = false, Default = "auto",
        HelpText = "Per-stream language option: auto | en | ru | es | ...")]
    public string Language { get; set; } = "auto";

    [Option("num-threads", Required = false, Default = 1,
        HelpText = "Number of CPU threads for the model.")]
    public int NumThreads { get; set; } = 1;

    [Option("decoding-method", Required = false, Default = "greedy_search",
        HelpText = "greedy_search | modified_beam_search")]
    public string DecodingMethod { get; set; } = "greedy_search";

    [Option("max-active-paths", Required = false, Default = 4,
        HelpText = "Active paths for modified_beam_search.")]
    public int MaxActivePaths { get; set; } = 4;

    [Option("mic", Required = false, Default = -1,
        HelpText = "PortAudio input device index (default: system default).")]
    public int MicDevice { get; set; } = -1;

    [Option("wav", Required = false, HelpText = "Decode a 16 kHz mono PCM16 WAV file instead of the microphone.")]
    public string? WavFile { get; set; }

    [Option("wav-chunk-ms", Required = false, Default = 20,
        HelpText = "Chunk size in ms when streaming a WAV file.")]
    public int WavChunkMs { get; set; } = 20;

    [Option("wav-repeat", Required = false, Default = 1,
        HelpText = "Repeat the WAV file N times through the same session to simulate a long recording.")]
    public int WavRepeat { get; set; } = 1;

    [Option("output", Required = false, HelpText = "Write the final transcript to this UTF-8 file.")]
    public string? OutputFile { get; set; }

    [Option("concurrency-probe", Required = false, Default = false,
        HelpText = "Run the two-stream concurrency probe on the WAV file.")]
    public bool ConcurrencyProbe { get; set; }

    [Option("enable-endpoint", Required = false, Default = false,
        HelpText = "Enable sherpa endpoint detection.")]
    public bool EnableEndpoint { get; set; }
}
