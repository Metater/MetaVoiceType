using MetaVoiceType.Audio;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Models;
using MetaVoiceType.Sessions;
using MetaVoiceType.Storage;
using MetaVoiceType.Transcription;
using MetaVoiceType.VoiceCommands;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MetaVoiceType.Diagnostics;

public sealed partial class DiagnosticRunner(IAudioCaptureService audio, IModelDownloadService downloads, VoskCommandRecognizer vosk,
    PasteCoordinator paste, RecoveryWriter recovery, AppPaths paths, SherpaRuntimeBootstrapper runtime,
    ILoggerFactory loggerFactory, ILogger<DiagnosticRunner> logger)
{
    public async Task<int> RunAsync(StartupOptions options, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AudioDevice> devices = audio.EnumerateDevices();
        foreach (AudioDevice device in devices)
        {
            Console.WriteLine($"{(device.IsDefault ? "*" : " ")} {device.Name} [{device.Id}]");
            LogAudioDevice(logger, device.Name, device.IsDefault);
        }
        ModelCatalog catalog = ModelCatalog.LoadBundled();
        VoiceCommandCatalog commands = VoiceCommandCatalog.LoadBundled();
        VoiceCommandLanguage commandLanguage = commands.Get(options.CommandLanguage);
        string modelId = options.DictationLanguage.Equals("en", StringComparison.OrdinalIgnoreCase) || options.DictationLanguage.Equals("english", StringComparison.OrdinalIgnoreCase)
            ? "parakeet-v2" : "parakeet-v3";
        ModelArtifact model = catalog.Get(modelId);
        ModelArtifact vad = catalog.Get("silero-vad");
        if (options.InstallModels) await InstallModelsAsync(catalog, model, commandLanguage, cancellationToken).ConfigureAwait(false);

        string modelPath = Path.Combine(paths.DictationModels, model.ExpectedDirectory);
        string vadDirectory = Path.Combine(paths.DictationModels, vad.ExpectedDirectory);
        bool dictationInstalled = IsInstalled(modelPath, model) && IsInstalled(vadDirectory, vad);

        if (options.RecoveryCrashSeconds > 0)
        {
            if (!dictationInstalled) throw new InvalidOperationException("Install the selected Parakeet model and Silero VAD before creating a recovery fixture.");
            using var backend = new SherpaParakeetBackend(modelPath, model, runtime, loggerFactory.CreateLogger<SherpaParakeetBackend>());
            using var session = new DictationSession(options.DictationLanguage, 0, backend, Path.Combine(vadDirectory, vad.Files.Model!));
            recovery.Start();
            void CaptureRecovery(object? sender, AudioFrame frame) { session.Accept(frame); recovery.Enqueue(session, frame); }
            audio.FrameReady += CaptureRecovery;
            await audio.StartAsync(null, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(options.RecoveryCrashSeconds), cancellationToken).ConfigureAwait(false);
            Environment.FailFast("Intentional MetaVoiceType recovery diagnostic crash.");
            return 1;
        }

        if (options.PasteText is not null)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            PasteRequestResult result = paste.Queue(options.PasteText, () => { completed.TrySetResult(); return Task.CompletedTask; });
            if (result != PasteRequestResult.Accepted) throw new InvalidOperationException($"Diagnostic paste was rejected: {result}.");
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Paste transaction complete: {clock.Elapsed.TotalMilliseconds:F1} ms");
            return 0;
        }
        if (!options.SelfTest && !options.InstallModels && options.AudioFile is null && options.StressMinutes == 0) return 0;

        LogCatalogs(logger, commands.Languages.Count, model.Id);
        if (devices.Count == 0) throw new InvalidOperationException("No active Windows capture device was found.");
        foreach (AudioDevice device in devices)
        {
            long before = audio.Metrics.FramesCaptured;
            try { await audio.StartAsync(device.Id, cancellationToken).ConfigureAwait(false); await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
            finally { await audio.StopAsync(cancellationToken).ConfigureAwait(false); }
            if (audio.Metrics.FramesCaptured == before) throw new InvalidOperationException($"Audio device '{device.Name}' produced no frames.");
        }
        AudioMetrics metrics = audio.Metrics;
        if (metrics.LostFrames != 0) throw new InvalidOperationException($"Audio frames were lost during capture: {metrics.LostFrames}.");

        AudioFrame[]? testFrames = options.AudioFile is null ? null : ReadAudio(options.AudioFile);
        if (dictationInstalled)
        {
            using var backend = new SherpaParakeetBackend(modelPath, model, runtime, loggerFactory.CreateLogger<SherpaParakeetBackend>());
            Console.WriteLine($"ASR: {backend.Status.CompactLabel}; provider={backend.Status.Provider}; GPU={backend.Status.GpuName ?? "n/a"}; runtime={backend.Status.RuntimeVersion}");
            if (backend.Status.FallbackReason is not null) Console.WriteLine("Provider fallback: " + backend.Status.FallbackReason);
            if (testFrames is not null)
            {
                string text = backend.Transcribe(testFrames.SelectMany(x => x.Samples).ToArray());
                Console.WriteLine($"{backend.Status.ModelDisplayName}: {text}");
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Parakeet produced no text for the supplied audio file.");
            }
        }
        else if (testFrames is not null) throw new InvalidOperationException("Install the selected Parakeet model and Silero VAD before testing audio.");

        string commandPath = Path.Combine(paths.VoskModels, commandLanguage.ModelName);
        if (Directory.Exists(commandPath))
        {
            IReadOnlyDictionary<VoiceCommand, string> phrases = VoiceCommandKeys.All.ToDictionary(x => x.Key, x => commandLanguage.Commands[x.Value]);
            VoiceCommandMatch? recognized = null;
            vosk.CommandRecognized += (_, match) => recognized ??= match;
            vosk.Load(commandPath, phrases, commandLanguage.RestrictedGrammar != "unrestricted");
            if (testFrames is not null && options.TestCommand)
            {
                foreach (AudioFrame frame in testFrames) vosk.Accept(frame);
                for (int i = 0; i < 100; i++) vosk.Accept(Pcm16Converter.Convert(new byte[640]));
                Console.WriteLine("Vosk command: " + (recognized?.Phrase ?? "<none>"));
                if (recognized is null) throw new InvalidOperationException("Vosk did not recognize a configured command.");
            }
        }
        else if (options.TestCommand) throw new InvalidOperationException($"Install the {commandLanguage.DisplayName} Vosk model first.");
        if (options.StressMinutes > 0)
        {
            if (!dictationInstalled || !Directory.Exists(commandPath)) throw new InvalidOperationException("Install both selected models before a stress run.");
            await RunStressAsync(modelPath, model, Path.Combine(vadDirectory, vad.Files.Model!), options, cancellationToken).ConfigureAwait(false);
        }
        LogSelfTest(logger, metrics.FramesCaptured, metrics.MaxQueueDepth, metrics.CallbackMilliseconds, dictationInstalled);
        return 0;
    }

    private async Task RunStressAsync(string modelPath, ModelArtifact model, string vadPath, StartupOptions options, CancellationToken cancellationToken)
    {
        using var backend = new SherpaParakeetBackend(modelPath, model, runtime, loggerFactory.CreateLogger<SherpaParakeetBackend>());
        using var session = new DictationSession(options.DictationLanguage, 0, backend, vadPath);
        await using var coordinator = new DecodeCoordinator(loggerFactory.CreateLogger<DecodeCoordinator>());
        coordinator.Start();
        int commandTriggers = 0;
        vosk.CommandRecognized += (_, _) => Interlocked.Increment(ref commandTriggers);
        void OnFrame(object? sender, AudioFrame frame) { vosk.Accept(frame); coordinator.Enqueue(session, session.Accept(frame)); }
        audio.FrameReady += OnFrame;
        long startedMemory = Environment.WorkingSet;
        try
        {
            await audio.StartAsync(null, cancellationToken).ConfigureAwait(false);
            for (int minute = 1; minute <= options.StressMinutes; minute++)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                AudioMetrics current = audio.Metrics;
                Console.WriteLine($"Stress {minute}/{options.StressMinutes}: frames={current.FramesCaptured}, queue={current.QueueDepth}, maxQueue={current.MaxQueueDepth}, lost={current.LostFrames}, asrQueue={coordinator.QueueDepth}");
            }
        }
        finally { audio.FrameReady -= OnFrame; await audio.StopAsync(cancellationToken).ConfigureAwait(false); }
        coordinator.Finalize(session, session.Stop(false, false));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while (session.Status == DictationStatus.Finalizing) await Task.Delay(20, timeout.Token).ConfigureAwait(false);
        AudioMetrics final = audio.Metrics;
        Console.WriteLine($"Stress complete: provider={backend.Status.Provider}, status={session.Status}, finalizationMs={session.FinalizationMilliseconds:F1}, lost={final.LostFrames}, commandTriggers={commandTriggers}, memoryDeltaBytes={Environment.WorkingSet - startedMemory}");
        if (final.LostFrames != 0 || final.QueueDepth != 0 || coordinator.QueueDepth != 0) throw new InvalidOperationException("A real-time queue did not drain losslessly.");
    }

    private async Task InstallModelsAsync(ModelCatalog catalog, ModelArtifact dictation, VoiceCommandLanguage language, CancellationToken cancellationToken)
    {
        await downloads.InstallAsync(new(language.ArchiveUrl, language.ArchiveType, language.ModelName, paths.VoskModels, null,
            language.SizeBytes, ["am/final.mdl", "conf/mfcc.conf"]), Progress("Vosk"), cancellationToken).ConfigureAwait(false);
        IEnumerable<ModelArtifact> artifacts = new[] { catalog.Get("silero-vad"), dictation };
        if (!runtime.ForceCpu && runtime.ProbeNvidiaGpu() is not null) artifacts = new[] { catalog.Get("sherpa-cuda-12") }.Concat(artifacts);
        foreach (ModelArtifact artifact in artifacts)
        {
            string root = artifact.Kind == ModelArtifactKinds.Runtime ? paths.RuntimeModels : paths.DictationModels;
            await downloads.InstallAsync(artifact.ToInstallRequest(root), Progress(artifact.DisplayName), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsInstalled(string directory, ModelArtifact artifact) => artifact.RequiredFiles.All(file => File.Exists(Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar))));
    private static Progress<ModelDownloadProgress> Progress(string name) => new(value => Console.WriteLine($"{name}: {value.Stage} {(value.Percentage is null ? "" : $"{value.Percentage:F1}%")}"));
    private static AudioFrame[] ReadAudio(string file)
    {
        using var reader = new AudioFileReader(file);
        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels == 2) provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (provider.WaveFormat.Channels != 1) throw new InvalidDataException("Diagnostic audio must be mono or stereo.");
        if (provider.WaveFormat.SampleRate != AudioFrame.SampleRate) provider = new WdlResamplingSampleProvider(provider, AudioFrame.SampleRate);
        var frames = new List<AudioFrame>();
        var buffer = new float[320];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            float[] exact = buffer.AsSpan(0, read).ToArray();
            frames.Add(new(exact, Pcm16Converter.ToPcm16(exact), DateTimeOffset.UtcNow));
        }
        return frames.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audio device: {Device} default={Default}")]
    private static partial void LogAudioDevice(ILogger logger, string device, bool @default);
    [LoggerMessage(Level = LogLevel.Information, Message = "Catalog self-test passed: {Languages} command languages; dictation model {Model}")]
    private static partial void LogCatalogs(ILogger logger, int languages, string model);
    [LoggerMessage(Level = LogLevel.Information, Message = "Self-test passed: frames={Frames}, maxQueue={Queue}, callbackMs={Callback:F3}, dictationInstalled={Installed}")]
    private static partial void LogSelfTest(ILogger logger, long frames, int queue, double callback, bool installed);
}
