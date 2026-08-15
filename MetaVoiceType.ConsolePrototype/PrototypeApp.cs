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
            await ListDevicesAsync().ConfigureAwait(false);
            return 0;
        }

        if (_opts.UnitTests)
            return await UnitTests.RunAsync(_log, ct).ConfigureAwait(false);

        if (_opts.SoakMinutes > 0)
            return await RunSoakAsync(ct).ConfigureAwait(false);

        var config = BuildRecognizerConfig();
        _log.LogInformation("Creating recognizer (provider={Provider}, device={Device})...",
            _opts.Provider, _opts.Device);
        var sw = Stopwatch.StartNew();
        using var backend = new SherpaAsrBackend(config, _log);
        sw.Stop();
        _log.LogInformation("Backend ready in {Ms:F1}ms total.", sw.Elapsed.TotalMilliseconds);

        if (_opts.Phase2)
        {
            var harness = new Phase2Harness(_opts, _log, backend);
            return await harness.RunAsync(ct).ConfigureAwait(false);
        }

        if (_opts.Phase3)
        {
            var harness = new Phase3Harness(_opts, _log, backend);
            return await harness.RunAsync(ct).ConfigureAwait(false);
        }

        if (_opts.WavFile is not null)
        {
            string text = await RunWavAsync(backend, ct).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine("FINAL TRANSCRIPT:");
            Console.WriteLine(text);
            if (_opts.OutputFile is not null)
                await File.WriteAllTextAsync(_opts.OutputFile, text, Encoding.UTF8, ct).ConfigureAwait(false);
            return 0;
        }

        return await RunMicrophoneAsync(backend, ct).ConfigureAwait(false);
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

    private async Task ListDevicesAsync()
    {
        Console.WriteLine("=== NAudio (WASAPI) capture devices ===");
        using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
        {
            var devices = enumerator.EnumerateAudioEndPoints(
                NAudio.CoreAudioApi.DataFlow.Capture,
                NAudio.CoreAudioApi.DeviceState.Active).ToList();
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(
                NAudio.CoreAudioApi.DataFlow.Capture,
                NAudio.CoreAudioApi.Role.Console);
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                Console.WriteLine($"  [{i}]{(d.ID == defaultDevice.ID ? " (default)" : "")} {d.FriendlyName}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== PortAudio devices (comparison) ===");
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

    // ---------------------------------------------------------------- soak

    private async Task<int> RunSoakAsync(CancellationToken ct)
    {
        _log.LogInformation("SOAK: capturing from microphone for {Minutes} minutes (capture only, no ASR).",
            _opts.SoakMinutes);

        using IAudioSource source = CreateMicSource();
        long frames = 0;
        long maxDepth = 0;
        int dropped = 0;
        var sw = Stopwatch.StartNew();
        var deadline = TimeSpan.FromMinutes(_opts.SoakMinutes);

        try
        {
            await foreach (float[] frame in source.ReadFramesAsync(ct).ConfigureAwait(false))
            {
                frames++;
                if (source is NaudioCaptureSource n)
                {
                    maxDepth = Math.Max(maxDepth, n.MaxObservedDepth);
                    dropped = n.DroppedFrames;
                }
                else if (source is MicrophoneAudioSource m)
                {
                    maxDepth = Math.Max(maxDepth, m.MaxObservedDepth);
                    dropped += m.DrainDroppedFrameCount();
                }
                if (sw.Elapsed >= deadline)
                    break;
                if (frames % 1000 == 0)
                    _log.LogInformation("SOAK: {Frames} frames, {Sec:F0}s elapsed, maxQueue={Depth}, dropped={Dropped}",
                        frames, sw.Elapsed.TotalSeconds, maxDepth, dropped);
            }
        }
        finally
        {
            sw.Stop();
        }

        double minutes = sw.Elapsed.TotalMinutes;
        _log.LogInformation(
            "SOAK COMPLETE: {Minutes:F1} min, {Frames} frames, maxQueueDepth={Depth}, dropped={Dropped}",
            minutes, frames, maxDepth, dropped);

        Console.WriteLine();
        Console.WriteLine("=== MICROPHONE SOAK RESULT ===");
        Console.WriteLine($"Backend           : {source.GetType().Name}");
        Console.WriteLine($"Duration          : {minutes:F1} min");
        Console.WriteLine($"Frames captured   : {frames}");
        Console.WriteLine($"Max queue depth   : {maxDepth}");
        Console.WriteLine($"Frames dropped    : {dropped}");
        Console.WriteLine(dropped == 0 ? "VERDICT: PASS" : "VERDICT: FAIL");
        return dropped == 0 ? 0 : 1;
    }

    private IAudioSource CreateMicSource()
    {
        string backend = _opts.MicBackend.ToLowerInvariant();
        return backend switch
        {
            "naudio" or "wasapi" or "" => new NaudioCaptureSource(_opts.MicDevice, _log),
            "portaudio" or "pa" => new MicrophoneAudioSource(_opts.MicDevice, _log),
            _ => throw new ArgumentException(
                $"Unknown microphone backend '{_opts.MicBackend}'. Use 'naudio' or 'portaudio'.")
        };
    }

    // -------------------------------------------------------------------- WAV

    private async Task<string> RunWavAsync(SherpaAsrBackend backend, CancellationToken ct)
    {
        _log.LogInformation("Streaming WAV {Path} in {Chunk}ms chunks, {Repeat}x repeat.",
            _opts.WavFile, _opts.WavChunkMs, _opts.WavRepeat);
        using var source = new WavFileSource(_opts.WavFile!, _opts.WavChunkMs, _opts.WavRepeat, _log);

        // Phase 2 architecture for the single-WAV convenience path as well.
        await using var worker = new DecodeWorker();
        var coordinator = new SessionCoordinator(backend, worker);
        RecordingSession session = coordinator.TryStart(_opts.Language)
            ?? throw new InvalidOperationException("could not start session");
        var pump = new CapturePump(source, coordinator, boundSession: session);
        var pumpTask = pump.RunAsync(ct);

        // Feed the whole source, then stop and finalize.
        await pumpTask.ConfigureAwait(false);
        session.Stop();
        worker.SignalFinalize(session);

        while (session.State == SessionState.Finalizing && !ct.IsCancellationRequested)
            await Task.Delay(10, ct).ConfigureAwait(false);

        string text = session.FinalTranscript;
        _log.LogInformation(
            "WAV run: {AudioSec:F1}s audio; finalization {FinalMs:F1}ms.",
            session.AudioSecondsFed, session.FinalizationLatencyMs);

        await worker.DisposeAsync().ConfigureAwait(false);
        return text;
    }

    // ------------------------------------------------------------ microphone

    private async Task<int> RunMicrophoneAsync(SherpaAsrBackend backend, CancellationToken ct)
    {
        using IAudioSource source = CreateMicSource();
        await using var worker = new DecodeWorker();
        var coordinator = new SessionCoordinator(backend, worker);

        RecordingSession? session = coordinator.TryStart(_opts.Language);
        if (session is null)
            throw new InvalidOperationException("could not start session");

        string lastPartial = string.Empty;
        var renderSw = Stopwatch.StartNew();
        long? startedAt = null;

        var keyboardTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                {
                    _log.LogInformation("Enter pressed — stopping active session.");
                    RecordingSession? active = coordinator.Active;
                    if (active is { IsRecording: true })
                    {
                        active.Stop();
                        worker.SignalFinalize(active);
                    }
                    // Start a fresh session immediately: Enter = stop AND new
                    // session, proving the slot is free while the old one
                    // finalizes. (Phase 2 demonstration.)
                    coordinator.TryStart(_opts.Language);
                    return;
                }
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }, ct);

        try
        {
            var pump = new CapturePump(source, coordinator);
            var pumpTask = pump.RunAsync(ct);
            while (!pumpTask.IsCompleted)
            {
                RecordingSession? active = coordinator.Active;
                if (active is { IsRecording: true })
                {
                    string partial = active.PartialTranscript;
                    if (partial != lastPartial)
                    {
                        lastPartial = partial;
                        renderSw.Restart();
                        if (startedAt is { } start)
                        {
                            double audioSec = (Environment.TickCount64 - start) / 1000.0;
                            double lagSec = Math.Max(audioSec - active.AudioSecondsFed, 0);
                            int dropped = source switch
                            {
                                NaudioCaptureSource n => n.DrainDroppedFrameCount(),
                                MicrophoneAudioSource m => m.DrainDroppedFrameCount(),
                                _ => 0
                            };
                            long captureDepth = source switch
                            {
                                NaudioCaptureSource n => n.MaxObservedDepth,
                                MicrophoneAudioSource m => m.MaxObservedDepth,
                                _ => 0
                            };
                            Console.Write($"\r[{audioSec,6:F1}s] capQ={captureDepth,2} " +
                                $"asrQ={worker.QueueDepth,2} decode={worker.LastDecodeMs,6:F2}ms " +
                                $"lag={lagSec,4:F1}s dropped={dropped,3} | {Truncate(lastPartial, 90)}    ");
                        }
                    }
                }
                else
                {
                    startedAt = null;
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            await pumpTask.ConfigureAwait(false);
        }
        finally
        {
            await keyboardTask.ConfigureAwait(false);
        }

        // Finalize whatever is active at exit.
        RecordingSession? final = coordinator.Active;
        if (final is { IsRecording: true })
        {
            final.Stop();
            worker.SignalFinalize(final);
        }

        while (coordinator.All.Any(s => s.IsFinalizing) && !ct.IsCancellationRequested)
            await Task.Delay(10, ct).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("SESSIONS:");
        foreach (RecordingSession s in coordinator.All)
        {
            Console.WriteLine($"  {s.Id} [{s.State}] {Truncate(s.FinalTranscript, 80)}");
        }

        if (_opts.OutputFile is not null && final is not null)
            await File.WriteAllTextAsync(_opts.OutputFile, final.FinalTranscript, Encoding.UTF8, ct).ConfigureAwait(false);

        await worker.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
