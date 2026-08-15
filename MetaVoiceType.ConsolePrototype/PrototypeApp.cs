using System.Diagnostics;
using System.Text;
using PortAudioSharp;
using SherpaOnnx;

namespace MetaVoiceType.ConsolePrototype;

public sealed class PrototypeApp
{
    private readonly Options _opts;
    private readonly ILogger _log;

    public PrototypeApp(Options opts, ILogger log)
    {
        _opts = opts;
        _log = log;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (_opts.ListDevices)
        {
            ListDevices();
            return 0;
        }

        _log.LogInformation("sherpa-onnx version: {Sherpa}", SherpaOnnx.VersionInfo.Version);
        _log.LogInformation("onnxruntime version: {Ort}", SherpaOnnx.VersionInfo.OnnxruntimeVersion);

        var config = BuildRecognizerConfig();
        _log.LogInformation("Creating recognizer (provider={Provider}, device={Device}, language={Lang})...",
            _opts.Provider, _opts.Device, _opts.Language);
        var sw = Stopwatch.StartNew();
        using var engine = new TranscriptionEngine(config, _log);
        sw.Stop();
        _log.LogInformation("Recognizer created in {Ms:F1}ms.", sw.Elapsed.TotalMilliseconds);

        if (_opts.ConcurrencyProbe)
        {
            await ConcurrencyProbe.RunAsync(_opts, _log, ct).ConfigureAwait(false);
            return 0;
        }

        if (_opts.WavFile is not null)
        {
            string text = await RunWavAsync(engine, ct).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine("FINAL TRANSCRIPT:");
            Console.WriteLine(text);
            if (_opts.OutputFile is not null)
                await File.WriteAllTextAsync(_opts.OutputFile, text, Encoding.UTF8, ct).ConfigureAwait(false);
            return 0;
        }

        return await RunMicrophoneAsync(engine, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ config

    private OnlineRecognizerConfig BuildRecognizerConfig()
    {
        var cfg = new OnlineRecognizerConfig
        {
            FeatConfig = { SampleRate = 16000, FeatureDim = 80 },
            ModelConfig =
            {
                Transducer =
                {
                    Encoder = _opts.Encoder,
                    Decoder = _opts.Decoder,
                    Joiner = _opts.Joiner
                },
                Tokens = _opts.Tokens,
                Provider = _opts.Provider,
                NumThreads = _opts.NumThreads,
                Debug = 0
            },
            DecodingMethod = _opts.DecodingMethod,
            MaxActivePaths = _opts.MaxActivePaths,
            EnableEndpoint = _opts.EnableEndpoint ? 1 : 0,
            Rule1MinTrailingSilence = 2.4f,
            Rule2MinTrailingSilence = 1.2f,
            Rule3MinUtteranceLength = 300f
        };
        return cfg;
    }

    // ---------------------------------------------------------------- devices

    private void ListDevices()
    {
        PortAudio.Initialize();
        try
        {
            Console.WriteLine($"PortAudio version: {PortAudio.VersionInfo.versionText}");
            Console.WriteLine($"Number of devices: {PortAudio.DeviceCount}");
            int defaultInput = PortAudio.DefaultInputDevice;
            for (int i = 0; i != PortAudio.DeviceCount; ++i)
            {
                DeviceInfo d = PortAudio.GetDeviceInfo(i);
                Console.WriteLine(
                    $"  [{i}]{(i == defaultInput ? " (default input)" : "")} " +
                    $"{d.name} | in={d.maxInputChannels} out={d.maxOutputChannels} " +
                    $"rate={d.defaultSampleRate}");
            }
        }
        finally
        {
            PortAudio.Terminate();
        }
    }

    // -------------------------------------------------------------------- WAV

    private async Task<string> RunWavAsync(TranscriptionEngine engine, CancellationToken ct)
    {
        _log.LogInformation("Streaming WAV {Path} in {Chunk}ms chunks, {Repeat}x repeat.",
            _opts.WavFile, _opts.WavChunkMs, _opts.WavRepeat);
        using var source = new WavFileSource(_opts.WavFile!, _opts.WavChunkMs, _opts.WavRepeat, _log);
        using var session = engine.CreateSession("wav", _opts.Language);

        var sw = Stopwatch.StartNew();
        long lastRenderMs = 0;

        await foreach (float[] frame in source.ReadFramesAsync(ct).ConfigureAwait(false))
        {
            session.Feed(frame, source.SampleRate);
            string partial = engine.Process(session);

            if (sw.ElapsedMilliseconds - lastRenderMs >= 250)
            {
                lastRenderMs = sw.ElapsedMilliseconds;
                if (session.HasNewPartial(partial))
                    session.CommitPartial(partial);
                _log.LogInformation("[{AudioSec:F1}s] {Text}", session.AudioSecondsFed, Truncate(partial, 90));
            }
        }

        var result = engine.FinalizeBlocking(session);
        sw.Stop();

        _log.LogInformation(
            "WAV run: {AudioSec:F1}s audio, {ProcessMs:F0}ms processing, " +
            "{Rate:F2} ms processing per audio second.",
            session.AudioSecondsFed,
            session.GetProcessMsPerAudioSecond() * session.AudioSecondsFed,
            session.GetProcessMsPerAudioSecond());

        return result.Text;
    }

    // ------------------------------------------------------------ microphone

    private async Task<int> RunMicrophoneAsync(TranscriptionEngine engine, CancellationToken ct)
    {
        using var source = new MicrophoneAudioSource(_opts.MicDevice, _log);
        using var session = engine.CreateSession("mic", _opts.Language);

        string lastPartial = string.Empty;
        var renderSw = Stopwatch.StartNew();
        long? startedAt = null;
        bool finalizing = false;
        string finalText = string.Empty;
        var finalizeSw = new Stopwatch();

        var keyboardTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested && !finalizing)
            {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                {
                    _log.LogInformation("Enter pressed — finalizing session.");
                    finalizeSw.Start();
                    var result = engine.FinalizeBlocking(session);
                    finalizeSw.Stop();
                    finalText = result.Text;
                    finalizing = true;
                    return;
                }
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }, ct);

        try
        {
            await foreach (float[] frame in source.ReadFramesAsync(ct).ConfigureAwait(false))
            {
                startedAt ??= Environment.TickCount64;
                session.Feed(frame, source.SampleRate);
                string partial = engine.Process(session);

                if (renderSw.ElapsedMilliseconds >= 250)
                {
                    renderSw.Restart();
                    if (session.HasNewPartial(partial))
                    {
                        session.CommitPartial(partial);
                        lastPartial = partial;
                    }

                    double audioSec = (Environment.TickCount64 - startedAt.Value) / 1000.0;
                    double lagSec = Math.Max(audioSec - session.AudioSecondsFed, 0);
                    int dropped = ((MicrophoneAudioSource)source).DrainDroppedFrameCount();

                    Console.Write($"\r[{audioSec,6:F1}s] proc={session.GetProcessMsPerAudioSecond(),5:F2}ms/s " +
                        $"lag={lagSec,4:F1}s dropped={dropped,3} | {Truncate(lastPartial, 100)}    ");
                }

                if (finalizing)
                    break;
            }
        }
        finally
        {
            await keyboardTask.ConfigureAwait(false);
        }

        if (!finalizing)
        {
            // Capture ended without Enter (Ctrl+C path): still finalize.
            finalizeSw.Start();
            var result = engine.FinalizeBlocking(session);
            finalizeSw.Stop();
            finalText = result.Text;
        }

        Console.WriteLine();
        Console.WriteLine("FINAL TRANSCRIPT:");
        Console.WriteLine(finalText);
        Console.WriteLine();
        _log.LogInformation(
            "Finalization took {Ms:F1}ms. Processing cost: {Rate:F2} ms per audio second.",
            finalizeSw.Elapsed.TotalMilliseconds, session.GetProcessMsPerAudioSecond());

        if (_opts.OutputFile is not null)
            await File.WriteAllTextAsync(_opts.OutputFile, finalText, Encoding.UTF8, ct).ConfigureAwait(false);
        return 0;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
