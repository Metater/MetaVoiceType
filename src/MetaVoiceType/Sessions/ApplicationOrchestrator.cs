using System.Text.Json;
using System.Collections.Concurrent;
using Avalonia.Threading;
using MetaVoiceType.Audio;
using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using MetaVoiceType.Core.State;
using MetaVoiceType.Models;
using MetaVoiceType.Storage;
using MetaVoiceType.Transcription;
using MetaVoiceType.VoiceCommands;
using Microsoft.Extensions.Logging;

namespace MetaVoiceType.Sessions;

public sealed partial class ApplicationOrchestrator : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IAudioCaptureService _audio;
    private readonly IHistoryStore _history;
    private readonly ISettingsStore _settingsStore;
    private readonly PasteCoordinator _paste;
    private readonly DecodeCoordinator _decode;
    private readonly RecoveryWriter _recovery;
    private readonly VoskCommandRecognizer _commands;
    private readonly IAudioCueService _cues;
    private readonly MetaVoiceTypeState _state;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ApplicationOrchestrator> _logger;
    private SherpaNemotronBackend? _backend;
    private DictationSession? _active;
    private AppSettings _settings = new();
    private readonly Dictionary<string, string> _acceptedStopPhrases = new();
    private string? _pendingPasteSessionId;
    private readonly HashSet<string> _canceledPasteSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _recoveryCloseTasks = new(StringComparer.Ordinal);
    private int _disposed;

    public ApplicationOrchestrator(IAudioCaptureService audio, IHistoryStore history, ISettingsStore settingsStore,
        PasteCoordinator paste, DecodeCoordinator decode, RecoveryWriter recovery, VoskCommandRecognizer commands, IAudioCueService cues,
        MetaVoiceTypeState state, ILoggerFactory loggerFactory, ILogger<ApplicationOrchestrator> logger)
    {
        _audio = audio; _history = history; _settingsStore = settingsStore; _paste = paste; _decode = decode; _recovery = recovery;
        _commands = commands; _cues = cues; _state = state; _loggerFactory = loggerFactory; _logger = logger;
        _audio.FrameReady += OnAudioFrame;
        _audio.LevelChanged += OnLevel;
        _decode.TranscriptChanged += OnTranscriptChanged;
        _decode.SessionCompleted += OnSessionCompleted;
        _commands.CommandRecognized += OnVoiceCommand;
    }

    public MetaVoiceTypeState State => _state;
    public AppSettings Settings => _settings;
    public bool IsTranscriptionReady => _backend is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TranscriptRecord> history = await _history.LoadAsync(cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => { foreach (TranscriptRecord record in history) _state.History.Add(record); });
        _decode.Start(); _recovery.Start();
        try
        {
            await _audio.StartAsync(_settings.AudioDeviceId, cancellationToken).ConfigureAwait(false);
            SetStatus("Download the command and dictation models to finish setup.");
        }
        catch (Exception ex)
        {
            LogMicrophoneFailed(_logger, ex);
            SetStatus("Microphone unavailable. Choose an active capture device in Settings.");
        }
    }

    public void InitializeNemotron(string modelDirectory)
    {
        var replacement = new SherpaNemotronBackend(modelDirectory, ModelCatalog.LoadBundled().Nemotron, _loggerFactory.CreateLogger<SherpaNemotronBackend>());
        lock (_gate)
        {
            if (_backend is not null)
            {
                replacement.Dispose();
                return;
            }
            _backend = replacement;
            _state.Acceleration = _backend.Acceleration;
        }
        SetStatus("Ready — say your start recording command.");
        _ = RecoverInterruptedAsync();
    }

    public void InitializeVosk(string modelDirectory, VoiceCommandLanguage language)
    {
        IReadOnlyDictionary<VoiceCommand, string> phrases = ResolvePhrases(language);
        _commands.Load(modelDirectory, phrases, language.RestrictedGrammar != "unrestricted");
        Dispatcher.UIThread.Post(() => _state.CommandListenerActive = true);
    }

    public IReadOnlyDictionary<VoiceCommand, string> ResolvePhrases(VoiceCommandLanguage language)
    {
        Dictionary<string, string>? overrides = _settings.CommandOverrides.GetValueOrDefault(language.Id);
        return VoiceCommandKeys.All.ToDictionary(x => x.Key, x => overrides?.GetValueOrDefault(x.Value) ?? language.Commands[x.Value]);
    }

    public async Task UpdateCommandPhrasesAsync(string languageId, IReadOnlyDictionary<VoiceCommand, string> phrases, CancellationToken cancellationToken = default)
    {
        CommandPhraseValidator.Validate(phrases.ToDictionary(x => VoiceCommandKeys.All[x.Key], x => x.Value));
        var overrides = _settings.CommandOverrides.ToDictionary(x => x.Key, x => new Dictionary<string, string>(x.Value), StringComparer.OrdinalIgnoreCase);
        overrides[languageId] = phrases.ToDictionary(x => VoiceCommandKeys.All[x.Key], x => x.Value);
        _settings = _settings with { CommandOverrides = overrides };
        await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        if (_settings.VoiceCommandLanguage.Equals(languageId, StringComparison.OrdinalIgnoreCase) && _commands.IsReady)
            _commands.RebuildGrammar(phrases);
    }

    public bool StartRecording()
    {
        lock (_gate)
        {
            if (_active is not null || _backend is null) return false;
            _active = new DictationSession(_settings.DictationLanguage, _backend.CreateStream(_settings.DictationLanguage));
            SetRecordingState(true, _active.StartedAt, "Recording…");
            LogSessionStarted(_logger, _active.Id, _active.Language);
            return true;
        }
    }

    public bool StopRecording(bool canceled = false, bool paste = false, string? acceptedPhrase = null)
    {
        DictationSession? session;
        lock (_gate)
        {
            session = _active;
            if (session is null) return false;
            _active = null;
            if (!string.IsNullOrWhiteSpace(acceptedPhrase)) _acceptedStopPhrases[session.Id] = acceptedPhrase;
            session.Stop(canceled, paste);
            _recoveryCloseTasks[session.Id] = _recovery.CloseAsync(session);
            _decode.Finalize(session);
            SetRecordingState(false, null, paste ? "Finalizing and preparing paste…" : "Finalizing…");
        }
        return true;
    }

    public PasteRequestResult PasteHere(string? acceptedPhrase = null)
    {
        lock (_gate)
        {
            if (_pendingPasteSessionId is not null || _paste.IsPending) return PasteRequestResult.AlreadyPending;
            if (_active is not null) _pendingPasteSessionId = _active.Id;
        }
        if (StopRecording(paste: true, acceptedPhrase: acceptedPhrase))
        {
            Dispatcher.UIThread.Post(() => _state.PastePending = true);
            return PasteRequestResult.Accepted;
        }
        TranscriptRecord? latest = _state.History.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Text));
        PasteRequestResult result = _paste.Queue(latest?.Text ?? "", () => MarkPastedAsync(latest));
        Dispatcher.UIThread.Post(() => _state.PastePending = result == PasteRequestResult.Accepted);
        return result;
    }

    public void CancelPaste()
    {
        lock (_gate)
        {
            if (_pendingPasteSessionId is not null) _canceledPasteSessions.Add(_pendingPasteSessionId);
            _pendingPasteSessionId = null;
        }
        _paste.Cancel(); Dispatcher.UIThread.Post(() => _state.PastePending = false);
    }

    public async Task UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(_settings.AudioDeviceId, settings.AudioDeviceId, StringComparison.Ordinal))
        {
            lock (_gate) if (_active is not null) throw new InvalidOperationException("Stop recording before changing the microphone.");
            string? previous = _settings.AudioDeviceId;
            await _audio.StopAsync(cancellationToken).ConfigureAwait(false);
            try { await _audio.StartAsync(settings.AudioDeviceId, cancellationToken).ConfigureAwait(false); }
            catch
            {
                await _audio.StartAsync(previous, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        _settings = settings;
        await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task CopyCurrentAsync(CancellationToken cancellationToken = default)
    {
        string text;
        lock (_gate) text = _active?.LiveText ?? _state.History.FirstOrDefault()?.Text ?? "";
        await _paste.CopyAsync(text, cancellationToken).ConfigureAwait(false);
        SetStatus(string.IsNullOrWhiteSpace(text) ? "Nothing to copy yet." : "Copied.");
    }

    public async Task HandleAsync(VoiceCommand command, string? acceptedPhrase = null)
    {
        bool accepted = true;
        switch (command)
        {
            case VoiceCommand.StartRecording: accepted = StartRecording(); break;
            case VoiceCommand.StopRecording: accepted = StopRecording(acceptedPhrase: acceptedPhrase); break;
            case VoiceCommand.PasteHere: accepted = PasteHere(acceptedPhrase) == PasteRequestResult.Accepted; break;
            case VoiceCommand.CancelRecording: accepted = StopRecording(canceled: true, acceptedPhrase: acceptedPhrase); break;
            case VoiceCommand.CancelPaste: accepted = _pendingPasteSessionId is not null || _paste.IsPending; if (accepted) CancelPaste(); break;
            case VoiceCommand.CopyRecordingToClipboard: await CopyCurrentAsync().ConfigureAwait(false); break;
        }
        if (accepted) _cues.PlayAccepted(command, _settings.CueVolume); else _cues.PlayError(_settings.CueVolume);
    }

    private void OnVoiceCommand(object? sender, VoiceCommandMatch match) => _ = HandleAsync(match.Command, match.Phrase);
    private void OnAudioFrame(object? sender, AudioFrame frame)
    {
        _commands.Accept(frame);
        DictationSession? session;
        lock (_gate) session = _active;
        if (session is null) return;
        session.Accept(frame); _recovery.Enqueue(session, frame); _decode.SignalLive(session);
    }
    private void OnLevel(object? sender, double level) => Dispatcher.UIThread.Post(() => _state.AudioLevel = level);
    private void OnTranscriptChanged(object? sender, DictationSession session) => Dispatcher.UIThread.Post(() => _state.LiveTranscript = session.LiveText);
    private void OnSessionCompleted(object? sender, DictationSession session) => _ = CommitCompletedAsync(session);

    private async Task CommitCompletedAsync(DictationSession session)
    {
        string text = TranscriptTailCleaner.RemoveAcceptedCommandTail(session.FinalText, _acceptedStopPhrases.Remove(session.Id, out string? phrase) ? phrase : null);
        DateTimeOffset stopped = session.StoppedAt ?? DateTimeOffset.UtcNow;
        var record = new TranscriptRecord(session.Id, session.StartedAt, stopped, session.Status, session.Language, text, session.Canceled, false, false);
        try
        {
            await _history.AddAsync(record).ConfigureAwait(false);
            if (_recoveryCloseTasks.TryRemove(session.Id, out Task? closeTask)) await closeTask.ConfigureAwait(false);
            _recovery.Delete(session.Id);
            await Dispatcher.UIThread.InvokeAsync(() => { _state.History.Insert(0, record); while (_state.History.Count > JsonHistoryStore.Retention) _state.History.RemoveAt(_state.History.Count - 1); _state.LiveTranscript = ""; });
            bool pasteCanceled;
            lock (_gate)
            {
                pasteCanceled = _canceledPasteSessions.Remove(session.Id);
                if (_pendingPasteSessionId == session.Id) _pendingPasteSessionId = null;
            }
            if (!session.Canceled && session.PasteRequested && !pasteCanceled)
            {
                _paste.Queue(text, () => MarkPastedAsync(record));
                Dispatcher.UIThread.Post(() => _state.PastePending = _paste.IsPending);
            }
            else if (!session.Canceled && _settings.CopyOnStop)
            {
                await _paste.CopyAsync(text).ConfigureAwait(false);
            }
            SetStatus(session.Canceled ? "Canceled recording saved to history." : "Recording saved.");
            LogSessionCompleted(_logger, session.Id, text.Length, session.FinalizationMilliseconds ?? 0);
        }
        catch (Exception ex) { LogCommitFailed(_logger, ex, session.Id); SetStatus("Could not save the transcript; recovery audio was preserved."); }
    }

    private Task MarkPastedAsync(TranscriptRecord? record)
    {
        Dispatcher.UIThread.Post(() => { _state.PastePending = false; _state.StatusMessage = "Pasted."; });
        return Task.CompletedTask;
    }

    private async Task RecoverInterruptedAsync()
    {
        foreach (string directory in _recovery.Discover())
        {
            try
            {
                string metadataPath = Path.Combine(directory, "session.json");
                RecoveryMetadata? metadata = JsonSerializer.Deserialize<RecoveryMetadata>(await File.ReadAllTextAsync(metadataPath).ConfigureAwait(false), AtomicJsonFile.Options);
                if (metadata is null || _backend is null) continue;
                var session = new DictationSession(metadata.Language, _backend.CreateStream(metadata.Language), metadata.SessionId, metadata.StartedAt);
                byte[] audio = await File.ReadAllBytesAsync(Path.Combine(directory, "audio.pcm")).ConfigureAwait(false);
                const int bytesPerFrame = 640;
                for (int offset = 0; offset < audio.Length; offset += bytesPerFrame)
                {
                    int length = Math.Min(bytesPerFrame, audio.Length - offset);
                    session.Accept(Pcm16Converter.Convert(audio.AsSpan(offset, length)));
                    _decode.SignalLive(session);
                }
                session.Stop(false, false); _decode.Finalize(session);
                _cues.PlayRecovered(_settings.CueVolume);
                SetStatus("Recovering an interrupted recording in the background…");
            }
            catch (Exception ex) { LogRecoveryFailed(_logger, ex, Path.GetFileName(directory)); }
        }
    }

    private void SetRecordingState(bool recording, DateTimeOffset? started, string status) => Dispatcher.UIThread.Post(() => { _state.IsRecording = recording; _state.RecordingStartedAt = started; _state.StatusMessage = status; if (recording) _state.LiveTranscript = ""; });
    private void SetStatus(string status) => Dispatcher.UIThread.Post(() => _state.StatusMessage = status);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _audio.FrameReady -= OnAudioFrame; _audio.LevelChanged -= OnLevel; _decode.TranscriptChanged -= OnTranscriptChanged; _decode.SessionCompleted -= OnSessionCompleted; _commands.CommandRecognized -= OnVoiceCommand;
        await _audio.DisposeAsync().ConfigureAwait(false); await _decode.DisposeAsync().ConfigureAwait(false); await _recovery.DisposeAsync().ConfigureAwait(false);
        _commands.Dispose(); _backend?.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} started (language={Language}).")]
    private static partial void LogSessionStarted(ILogger logger, string sessionId, string language);
    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} finalized (chars={Characters}, finalizationMs={FinalizationMs:F1}).")]
    private static partial void LogSessionCompleted(ILogger logger, string sessionId, int characters, double finalizationMs);
    [LoggerMessage(Level = LogLevel.Error, Message = "Session {SessionId} could not be committed; recovery audio retained.")]
    private static partial void LogCommitFailed(ILogger logger, Exception exception, string sessionId);
    [LoggerMessage(Level = LogLevel.Error, Message = "Interrupted session {SessionId} could not be recovered.")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception, string sessionId);
    [LoggerMessage(Level = LogLevel.Error, Message = "The configured microphone could not be started.")]
    private static partial void LogMicrophoneFailed(ILogger logger, Exception exception);
}
