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
    PasteCoordinator paste, RecoveryWriter recovery, AppPaths paths, ILoggerFactory loggerFactory, ILogger<DiagnosticRunner> logger)
{
    public async Task<int> RunAsync(StartupOptions options, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AudioDevice> devices = audio.EnumerateDevices();
        foreach (AudioDevice device in devices)
        {
            string line = $"{(device.IsDefault ? "*" : " ")} {device.Name} [{device.Id}]";
            Console.WriteLine(line);
            LogAudioDevice(logger, device.Name, device.IsDefault);
        }
        ModelCatalog models = ModelCatalog.LoadBundled();
        VoiceCommandCatalog commands = VoiceCommandCatalog.LoadBundled();
        VoiceCommandLanguage commandLanguage = commands.Get(options.CommandLanguage);
        IReadOnlyList<string> supportedLanguages = models.Nemotron.Languages.TranscriptionReady
            .Concat(models.Nemotron.Languages.BroadCoverage).Concat(models.Nemotron.Languages.AdaptationReady).ToArray();
        if (options.DictationLanguage != "auto" && !supportedLanguages.Contains(options.DictationLanguage, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported Nemotron diagnostic language '{options.DictationLanguage}'.");
        if (options.InstallModels) await InstallModelsAsync(models, commandLanguage, cancellationToken).ConfigureAwait(false);
        if (options.RecoveryCrashSeconds > 0)
        {
            string modelPath = Path.Combine(paths.NemotronModels, models.Nemotron.ExtractedDirectory);
            if (!models.Nemotron.RequiredFiles.All(file => File.Exists(Path.Combine(modelPath, file))))
                throw new InvalidOperationException("Install Nemotron before creating a recovery crash fixture.");
            using var backend = new SherpaNemotronBackend(modelPath, models.Nemotron, loggerFactory.CreateLogger<SherpaNemotronBackend>());
            using var session = new DictationSession(options.DictationLanguage, backend.CreateStream(options.DictationLanguage));
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

        LogCatalogs(logger, commands.Languages.Count, models.Nemotron.Id);
        if (devices.Count == 0) throw new InvalidOperationException("No active Windows capture device was found.");

        foreach (AudioDevice device in devices)
        {
            long before = audio.Metrics.FramesCaptured;
            try
            {
                await audio.StartAsync(device.Id, cancellationToken).ConfigureAwait(false);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            finally { await audio.StopAsync(cancellationToken).ConfigureAwait(false); }
            if (audio.Metrics.FramesCaptured == before) throw new InvalidOperationException($"Audio device '{device.Name}' produced no frames.");
        }
        AudioMetrics metrics = audio.Metrics;
        if (metrics.FramesCaptured == 0) throw new InvalidOperationException("The default microphone opened but produced no audio frames.");
        if (metrics.LostFrames != 0) throw new InvalidOperationException($"Audio frames were lost during capture: {metrics.LostFrames}.");

        string dictationPath = Path.Combine(paths.NemotronModels, models.Nemotron.ExtractedDirectory);
        bool dictationInstalled = models.Nemotron.RequiredFiles.All(file => File.Exists(Path.Combine(dictationPath, file)));
        AudioFrame[]? testFrames = options.AudioFile is null ? null : ReadAudio(options.AudioFile);
        if (dictationInstalled)
        {
            using var backend = new SherpaNemotronBackend(dictationPath, models.Nemotron, loggerFactory.CreateLogger<SherpaNemotronBackend>());
            using IAsrChannel channel = backend.CreateStream(options.DictationLanguage);
            if (testFrames is not null) foreach (AudioFrame frame in testFrames) channel.Accept(frame.Samples);
            channel.Accept(new float[16_000]);
            channel.Finish();
            while (channel.IsReady()) channel.Decode();
            if (testFrames is not null)
            {
                Console.WriteLine("Nemotron: " + channel.CurrentText);
                if (string.IsNullOrWhiteSpace(channel.CurrentText)) throw new InvalidOperationException("Nemotron produced no text for the supplied audio file.");
            }
        }
        else if (testFrames is not null) throw new InvalidOperationException("Install Nemotron before testing an audio file.");
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
                if (recognized is null) throw new InvalidOperationException("Vosk did not recognize a configured command in the supplied audio file.");
            }
        }
        else if (options.TestCommand) throw new InvalidOperationException($"Install the {commandLanguage.DisplayName} Vosk model before testing its command audio.");
        if (options.StressMinutes > 0)
        {
            if (!dictationInstalled || !Directory.Exists(commandPath)) throw new InvalidOperationException("Install both selected models before a stress run.");
            await RunStressAsync(dictationPath, models.Nemotron, options, cancellationToken).ConfigureAwait(false);
        }
        LogSelfTest(logger, metrics.FramesCaptured, metrics.MaxQueueDepth, metrics.CallbackMilliseconds, dictationInstalled);
        return 0;
    }

    private async Task RunStressAsync(string modelPath, DictationModel model, StartupOptions options, CancellationToken cancellationToken)
    {
        using var backend = new SherpaNemotronBackend(modelPath, model, loggerFactory.CreateLogger<SherpaNemotronBackend>());
        using var session = new DictationSession(options.DictationLanguage, backend.CreateStream(options.DictationLanguage));
        await using var coordinator = new DecodeCoordinator(loggerFactory.CreateLogger<DecodeCoordinator>());
        coordinator.Start();
        int commandTriggers = 0;
        vosk.CommandRecognized += (_, _) => Interlocked.Increment(ref commandTriggers);
        void OnFrame(object? sender, AudioFrame frame) { vosk.Accept(frame); session.Accept(frame); coordinator.SignalLive(session); }
        audio.FrameReady += OnFrame;
        long startedMemory = Environment.WorkingSet;
        try
        {
            await audio.StartAsync(null, cancellationToken).ConfigureAwait(false);
            for (int minute = 1; minute <= options.StressMinutes; minute++)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                AudioMetrics current = audio.Metrics;
                Console.WriteLine($"Stress {minute}/{options.StressMinutes} min: frames={current.FramesCaptured}, queue={current.QueueDepth}, maxQueue={current.MaxQueueDepth}, lost={current.LostFrames}");
            }
        }
        finally
        {
            audio.FrameReady -= OnFrame;
            await audio.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        session.Stop(false, false);
        coordinator.Finalize(session);
        using var finalizationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        finalizationTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (session.Status == DictationStatus.Finalizing) await Task.Delay(20, finalizationTimeout.Token).ConfigureAwait(false);
        AudioMetrics metrics = audio.Metrics;
        long memoryDelta = Environment.WorkingSet - startedMemory;
        Console.WriteLine($"Stress complete: status={session.Status}, finalizationMs={session.FinalizationMilliseconds:F1}, maxQueue={metrics.MaxQueueDepth}, lost={metrics.LostFrames}, commandTriggers={commandTriggers}, memoryDeltaBytes={memoryDelta}");
        if (metrics.LostFrames != 0 || metrics.QueueDepth != 0) throw new InvalidOperationException("The long-run audio queue did not remain lossless and drained.");
    }

    private async Task InstallModelsAsync(ModelCatalog catalog, VoiceCommandLanguage commandLanguage, CancellationToken cancellationToken)
    {
        var commandRequest = new ModelInstallRequest(commandLanguage.ArchiveUrl, commandLanguage.ArchiveType, commandLanguage.ModelName, paths.VoskModels, null,
            commandLanguage.SizeBytes, ["am/final.mdl", "conf/mfcc.conf"]);
        await downloads.InstallAsync(commandRequest, Progress("Vosk"), cancellationToken).ConfigureAwait(false);

        DictationModel model = catalog.Nemotron;
        var dictationRequest = new ModelInstallRequest(model.ArchiveUrl, model.ArchiveType, model.ExtractedDirectory, paths.NemotronModels,
            model.ArchiveSha256, model.EstimatedDownloadBytes, model.RequiredFiles);
        await downloads.InstallAsync(dictationRequest, Progress("Nemotron"), cancellationToken).ConfigureAwait(false);
    }

    private static Progress<ModelDownloadProgress> Progress(string name) => new(value =>
        Console.WriteLine($"{name}: {value.Stage} {(value.Percentage is null ? "" : $"{value.Percentage:F1}%")}"));

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
