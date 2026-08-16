using System.Collections.Concurrent;
using System.Text.Json;
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
    private readonly CustomCommandExecutor _customCommands;
    private readonly RecordingEventShortcutPlayer _recordingShortcuts;
    private readonly IAudioCueService _cues;
    private readonly MetaVoiceTypeState _state;
    private readonly SherpaRuntimeBootstrapper _runtime;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ApplicationOrchestrator> _logger;
    private readonly Dictionary<string, DictationSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<IAsrBackend, int> _backendUsers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IAsrBackend> _retiredBackends = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _textFallbackPhrases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _canceledPasteSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _recoveryCloseTasks = new(StringComparer.Ordinal);
    private IAsrBackend? _backend;
    private string? _vadModelPath;
    private DictationSession? _active;
    private string? _lastStoppedSessionId;
    private string? _pendingPasteSessionId;
    private string? _activeVoiceLanguageId;
    private VoiceCommandLanguage? _activeVoiceLanguage;
    private AppSettings _settings = new();
    private long _audioSampleClock;
    private long _commandSequence;
    private long _voskFrames;
    private long _vadFrames;
    private long _recoveryFrames;
    private readonly AudioPreRollBuffer _preRoll = new();
    private int _disposed;

    public ApplicationOrchestrator(IAudioCaptureService audio, IHistoryStore history, ISettingsStore settingsStore,
        PasteCoordinator paste, DecodeCoordinator decode, RecoveryWriter recovery, VoskCommandRecognizer commands,
        CustomCommandExecutor customCommands, RecordingEventShortcutPlayer recordingShortcuts,
        IAudioCueService cues, MetaVoiceTypeState state, SherpaRuntimeBootstrapper runtime,
        ILoggerFactory loggerFactory, ILogger<ApplicationOrchestrator> logger)
    {
        _audio = audio; _history = history; _settingsStore = settingsStore; _paste = paste; _decode = decode; _recovery = recovery;
        _commands = commands; _customCommands = customCommands; _recordingShortcuts = recordingShortcuts; _cues = cues;
        _state = state; _runtime = runtime; _loggerFactory = loggerFactory; _logger = logger;
        _audio.FrameReady += OnAudioFrame;
        _audio.LevelChanged += OnLevel;
        _decode.TranscriptChanged += OnTranscriptChanged;
        _decode.SessionCompleted += OnSessionCompleted;
        _commands.CommandRecognized += OnVoiceCommand;
        _paste.StateChanged += OnPasteStateChanged;
    }

    public MetaVoiceTypeState State => _state;
    public AppSettings Settings => _settings;
    public bool IsTranscriptionReady => _backend is not null && _vadModelPath is not null;
    public string? ActiveVoiceCommandLanguageId => _activeVoiceLanguageId;
    public bool HasNvidiaGpu => _runtime.ProbeNvidiaGpu() is not null;
    public PipelineMetrics PipelineMetrics
    {
        get
        {
            AudioMetrics audio = _audio.Metrics;
            return new(audio.FramesCaptured, audio.FramesDispatched, audio.FramesDropped, Interlocked.Read(ref _recoveryFrames),
                Interlocked.Read(ref _voskFrames), Interlocked.Read(ref _vadFrames), audio.CaptureQueueHighWaterMark, _decode.MaxQueueDepth, _recovery.MaxQueueDepth);
        }
    }
    internal Func<string, ISpeechSegmenter> SegmenterFactory { get; set; } = path => new SherpaVadSegmenter(path);

    internal void SetBackendForTesting(IAsrBackend backend, string vadModelPath = "test-vad")
    {
        lock (_gate)
        {
            IAsrBackend? previous = _backend;
            _backend = backend;
            _vadModelPath = vadModelPath;
            if (!_backendUsers.ContainsKey(backend)) _backendUsers[backend] = 0;
            if (previous is not null && !ReferenceEquals(previous, backend)) RetireBackend(previous);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        _runtime.SetUserForceCpu(_settings.ForceCpuOnly);
        IReadOnlyList<TranscriptRecord> history = await _history.LoadAsync(cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (TranscriptRecord record in history) _state.History.Add(record);
            _state.SelectedVoiceLanguageId = _settings.VoiceCommandLanguage;
        });
        _decode.Start();
        _recovery.Start();
        try
        {
            await _audio.StartAsync(_settings.AudioDeviceId, cancellationToken).ConfigureAwait(false);
            SetStatus("Finish setup to begin dictating.");
        }
        catch (Exception ex)
        {
            LogMicrophoneFailed(_logger, ex);
            SetStatus("Microphone unavailable. Choose a capture device in Settings.");
        }
    }

    public async Task InitializeParakeetAsync(string modelDirectory, ModelArtifact model, string vadDirectory, CancellationToken cancellationToken = default)
    {
        if (model.Kind != ModelArtifactKinds.Dictation) throw new ArgumentException("A dictation artifact is required.", nameof(model));
        string vadPath = Path.Combine(vadDirectory, ModelCatalog.LoadBundled().Get("silero-vad").Files.Model!);
        if (!File.Exists(vadPath)) throw new FileNotFoundException("Silero VAD is not installed.", vadPath);
        var replacement = await Task.Run(() => new SherpaParakeetBackend(modelDirectory, model, _runtime,
            _loggerFactory.CreateLogger<SherpaParakeetBackend>()), cancellationToken).ConfigureAwait(false);
        IAsrBackend? previous;
        lock (_gate)
        {
            previous = _backend;
            _backend = replacement;
            _vadModelPath = vadPath;
            if (!_backendUsers.ContainsKey(replacement)) _backendUsers[replacement] = 0;
            if (previous is not null && !ReferenceEquals(previous, replacement)) RetireBackend(previous);
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _state.Acceleration = replacement.Status.Acceleration;
            _state.EngineLabel = replacement.Status.CompactLabel;
            _state.ProviderFallbackReason = replacement.Status.FallbackReason;
            _state.DictationModelState = "Ready";
        });
        SetStatus("Ready");
        _ = RecoverInterruptedAsync();
    }

    public void InitializeVosk(string modelDirectory, VoiceCommandLanguage language)
    {
        IReadOnlyList<VoiceCommandDefinition> definitions = ResolveDefinitions(language);
        _commands.Load(modelDirectory, definitions, language.RestrictedGrammar != "unrestricted");
        lock (_gate) { _activeVoiceLanguageId = language.Id; _activeVoiceLanguage = language; }
        Dispatcher.UIThread.Post(() =>
        {
            _state.CommandListenerActive = true;
            _state.ActiveVoiceLanguageId = language.Id;
            _state.VoiceModelState = "Active";
        });
    }

    public IReadOnlyDictionary<VoiceCommand, string> ResolvePhrases(VoiceCommandLanguage language)
    {
        return ResolveAliases(language).ToDictionary(x => x.Key, x => x.Value[0]);
    }

    public IReadOnlyDictionary<VoiceCommand, IReadOnlyList<string>> ResolveAliases(VoiceCommandLanguage language)
    {
        Dictionary<string, List<string>>? overrides = _settings.CommandAliases.GetValueOrDefault(language.Id);
        return VoiceCommandKeys.All.ToDictionary(x => x.Key, x =>
        {
            if (overrides?.GetValueOrDefault(x.Value) is { Count: > 0 } configured) return (IReadOnlyList<string>)configured;
            var defaults = new List<string> { language.Commands[x.Value] };
            if (language.CommandAliases?.GetValueOrDefault(x.Value) is { Count: > 0 } catalogAliases) defaults.AddRange(catalogAliases);
            return (IReadOnlyList<string>)defaults.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        });
    }

    public IReadOnlyList<VoiceCommandDefinition> ResolveDefinitions(VoiceCommandLanguage language)
    {
        var definitions = ResolveAliases(language).SelectMany(x => x.Value.Select(alias => VoiceCommandDefinition.BuiltIn(x.Key, alias))).ToList();
        definitions.AddRange(_settings.CustomCommands.Where(x => x.Enabled && x.VoiceCommandLanguageId.Equals(language.Id, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Aliases.Select(alias => new VoiceCommandDefinition(x.Id, alias))));
        string[] normalized = definitions.Select(x => CommandPhraseValidator.Normalize(x.Phrase)).ToArray();
        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new InvalidDataException("Voice commands contain an ambiguous duplicate phrase.");
        return definitions;
    }

    public async Task UpdateCommandPhrasesAsync(string languageId, IReadOnlyDictionary<VoiceCommand, string> phrases, CancellationToken cancellationToken = default)
    {
        await UpdateCommandAliasesAsync(languageId, phrases.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)[x.Value]), cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateCommandAliasesAsync(string languageId, IReadOnlyDictionary<VoiceCommand, IReadOnlyList<string>> aliases, CancellationToken cancellationToken = default)
    {
        foreach (IReadOnlyList<string> values in aliases.Values) CommandPhraseValidator.ValidateAliases(values);
        string[] normalized = aliases.Values.SelectMany(x => x).Select(CommandPhraseValidator.Normalize).ToArray();
        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new InvalidDataException("Voice commands contain an ambiguous duplicate alias.");
        var configured = _settings.CommandAliases.ToDictionary(x => x.Key,
            x => x.Value.ToDictionary(y => y.Key, y => y.Value.ToList(), StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        configured[languageId] = aliases.ToDictionary(x => VoiceCommandKeys.All[x.Key], x => x.Value.ToList());
        await UpdateSettingsAsync(_settings with { CommandAliases = configured }, cancellationToken).ConfigureAwait(false);
    }

    public bool StartRecording(long? preRollAfterSample = null, TranscriptRecord? continuation = null) =>
        StartRecordingWithCue(VoiceCommand.StartRecording, preRollAfterSample, continuation);

    private bool StartRecordingWithCue(VoiceCommand cue, long? preRollAfterSample = null, TranscriptRecord? continuation = null)
    {
        DictationSession session;
        lock (_gate)
        {
            if (_active is not null || _backend is null || _vadModelPath is null) { _cues.PlayError(_settings.CueVolume); return false; }
            string language = _settings.DictationMode == DictationMode.English ? "en" : "auto";
            long startSample = preRollAfterSample is long boundary ? Math.Min(boundary, _audioSampleClock) : _audioSampleClock;
            session = new(language, startSample, _backend, SegmenterFactory(_vadModelPath), continuedRecord: continuation);
            _active = session;
            _sessions[session.Id] = session;
            _backendUsers[_backend] = _backendUsers.GetValueOrDefault(_backend) + 1;
            SetRecordingState(true, session.StartedAt, "Recording");
            LogSessionStarted(_logger, session.Id, language, _backend.Status.ModelId);
        }
        if (preRollAfterSample is long after)
            foreach (AudioFrame frame in _preRoll.Snapshot(after, Interlocked.Read(ref _audioSampleClock))) AcceptForSession(session, frame);
        _ = _recordingShortcuts.RecordingStartedAsync(session.Id, _settings.RecordingStartedShortcut);
        _cues.PlayAccepted(cue, _settings.CueVolume);
        return true;
    }

    public bool ContinueRecording(long? preRollAfterSample = null)
    {
        TranscriptRecord? latest = _state.History.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Text) && !x.Canceled);
        if (latest is null) { ShowFeedback("Nothing to continue"); _cues.PlayError(_settings.CueVolume); return false; }
        return StartRecordingWithCue(VoiceCommand.ContinueRecording, preRollAfterSample, latest);
    }

    public bool StopRecording(bool canceled = false, bool paste = false)
    {
        if (paste && _paste.Reserve() != PasteRequestResult.Accepted) { _cues.PlayError(_settings.CueVolume); return false; }
        DictationSession? session;
        IReadOnlyList<DictationSegment> tail;
        lock (_gate)
        {
            session = _active;
            if (session is null) { if (paste) _paste.Cancel(); _cues.PlayError(_settings.CueVolume); return false; }
            _active = null;
            _lastStoppedSessionId = session.Id;
            if (paste) { session.RequestPaste(); _pendingPasteSessionId = session.Id; }
            tail = session.Stop(canceled, paste);
            _recoveryCloseTasks[session.Id] = _recovery.CloseAsync(session);
            SetRecordingState(false, null, paste ? "Preparing paste…" : "Finalizing…");
        }
        _decode.Finalize(session, tail);
        _ = _recordingShortcuts.RecordingEndedAsync(session.Id, _settings.RecordingStoppedShortcut);
        _cues.PlayAccepted(canceled ? VoiceCommand.CancelRecording : paste ? VoiceCommand.PasteHere : VoiceCommand.StopRecording, _settings.CueVolume);
        return true;
    }

    public PasteRequestResult PasteHere()
    {
        DictationSession? target;
        lock (_gate)
        {
            if (_paste.IsActive) return PasteRequestResult.AlreadyPending;
            target = _active;
            if (target is null && _lastStoppedSessionId is not null) _sessions.TryGetValue(_lastStoppedSessionId, out target);
            if (target is not null && target.Status is DictationStatus.Recording or DictationStatus.Finalizing or DictationStatus.Completed)
            {
                if (ReferenceEquals(target, _active)) { }
                else
                {
                    if (!target.RequestPaste()) return PasteRequestResult.AlreadyPending;
                    PasteRequestResult reservation = _paste.Reserve();
                    if (reservation != PasteRequestResult.Accepted) return reservation;
                    _pendingPasteSessionId = target.Id;
                    _cues.PlayAccepted(VoiceCommand.PasteHere, _settings.CueVolume);
                    return PasteRequestResult.Accepted;
                }
            }
        }
        if (target is not null && ReferenceEquals(target, _active) && StopRecording(paste: true)) return PasteRequestResult.Accepted;
        TranscriptRecord? latest = _state.History.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Text));
        return PasteRecord(latest);
    }

    public PasteRequestResult PasteRecord(TranscriptRecord? record)
    {
        PasteRequestResult result = _paste.Queue(record?.Text ?? "", () => MarkPastedAsync(record));
        if (result == PasteRequestResult.Accepted) _cues.PlayAccepted(VoiceCommand.PasteHere, _settings.CueVolume);
        else _cues.PlayError(_settings.CueVolume);
        return result;
    }

    public Task CopyRecordAsync(TranscriptRecord record, CancellationToken cancellationToken = default) => _paste.CopyAsync(record.Text, cancellationToken);

    public async Task DeleteRecordAsync(TranscriptRecord record, CancellationToken cancellationToken = default)
    {
        await _history.DeleteAsync(record.LogicalId, cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => _state.History.Remove(record));
        ShowFeedback("Deleted");
    }

    public async Task DeleteAllHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _history.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(_state.History.Clear);
        ShowFeedback("History deleted");
    }

    public void CancelPaste()
    {
        lock (_gate)
        {
            if (_pendingPasteSessionId is not null) _canceledPasteSessions.Add(_pendingPasteSessionId);
            _pendingPasteSessionId = null;
        }
        _paste.Cancel();
        _cues.PlayAccepted(VoiceCommand.CancelPaste, _settings.CueVolume);
    }

    public async Task UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(_settings.AudioDeviceId, settings.AudioDeviceId, StringComparison.Ordinal))
        {
            lock (_gate) if (_active is not null) throw new InvalidOperationException("Stop recording before changing the microphone.");
            string? previous = _settings.AudioDeviceId;
            await _audio.StopAsync(cancellationToken).ConfigureAwait(false);
            try { await _audio.StartAsync(settings.AudioDeviceId, cancellationToken).ConfigureAwait(false); }
            catch { await _audio.StartAsync(previous, cancellationToken).ConfigureAwait(false); throw; }
        }
        _settings = settings with { SchemaVersion = 4 };
        _runtime.SetUserForceCpu(_settings.ForceCpuOnly);
        await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        VoiceCommandLanguage? active;
        lock (_gate) active = _activeVoiceLanguage;
        if (active is not null && _commands.IsReady) _commands.RebuildGrammar(ResolveDefinitions(active), active.RestrictedGrammar != "unrestricted");
    }

    public async Task CopyCurrentAsync(CancellationToken cancellationToken = default)
    {
        string text;
        lock (_gate)
        {
            text = _active is null
                ? _state.History.FirstOrDefault()?.Text ?? ""
                : string.Join(' ', new[] { _active.PreviousText, WordReplacementEngine.Apply(_active.SegmentText, _settings.WordReplacementGroups) }
                    .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        }
        await _paste.CopyAsync(text, cancellationToken).ConfigureAwait(false);
        ShowFeedback(string.IsNullOrWhiteSpace(text) ? "Nothing to copy yet" : "Copied");
        if (string.IsNullOrWhiteSpace(text)) _cues.PlayError(_settings.CueVolume);
        else _cues.PlayAccepted(VoiceCommand.CopyRecordingToClipboard, _settings.CueVolume);
    }

    public Task HandleAsync(VoiceCommand command) => HandleAsync(new VoiceCommandMatch(VoiceCommandKeys.All[command], command, "", "", 0, null, null, null, DateTimeOffset.UtcNow));

    public async Task HandleAsync(VoiceCommandMatch match)
    {
        Interlocked.Increment(ref _commandSequence);
        DictationSession? session;
        lock (_gate) session = _active;
        if (session is not null)
        {
            if (match.AudioStartSample is long start && match.AudioEndSample is long end)
                _decode.Enqueue(session, session.MarkControlSpan(start, end));
            else if (!string.IsNullOrWhiteSpace(match.Phrase))
                lock (_gate) _textFallbackPhrases[session.Id] = match.Phrase;
        }

        if (match.Command is null)
        {
            CustomVoiceCommand? custom = _settings.CustomCommands.FirstOrDefault(x => x.Id == match.CommandId && x.Enabled);
            if (custom is null) { _cues.PlayError(_settings.CueVolume); return; }
            try { await _customCommands.ExecuteAsync(custom).ConfigureAwait(false); _cues.PlayAccepted(VoiceCommand.CopyRecordingToClipboard, _settings.CueVolume); }
            catch (Exception ex) { LogCustomCommandFailed(_logger, ex, custom.Id); _cues.PlayError(_settings.CueVolume); }
            return;
        }

        switch (match.Command.Value)
        {
            case VoiceCommand.StartRecording: StartRecording(match.AudioEndSample); break;
            case VoiceCommand.ContinueRecording: ContinueRecording(match.AudioEndSample); break;
            case VoiceCommand.StopRecording: StopRecording(); break;
            case VoiceCommand.PasteHere: PasteHere(); break;
            case VoiceCommand.CancelRecording: StopRecording(canceled: true); break;
            case VoiceCommand.CancelPaste:
                if (_pendingPasteSessionId is not null || _paste.IsActive) CancelPaste(); else _cues.PlayError(_settings.CueVolume);
                break;
            case VoiceCommand.CopyRecordingToClipboard: await CopyCurrentAsync().ConfigureAwait(false); break;
        }
    }

    private void OnVoiceCommand(object? sender, VoiceCommandMatch match) => _ = HandleAsync(match);

    private void OnAudioFrame(object? sender, AudioFrame frame)
    {
        long startSample = Interlocked.Read(ref _audioSampleClock);
        long endSample = startSample + frame.Samples.LongLength;
        _preRoll.Add(startSample, frame);
        Interlocked.Exchange(ref _audioSampleClock, endSample);
        long sequence = Interlocked.Read(ref _commandSequence);
        _commands.Accept(frame);
        Interlocked.Increment(ref _voskFrames);
        if (Interlocked.Read(ref _commandSequence) != sequence) return;
        DictationSession? session;
        lock (_gate) session = _active;
        if (session is null) return;
        AcceptForSession(session, frame);
    }

    private void AcceptForSession(DictationSession session, AudioFrame frame)
    {
        IReadOnlyList<DictationSegment> segments = session.Accept(frame);
        Interlocked.Increment(ref _vadFrames);
        _recovery.Enqueue(session, frame);
        Interlocked.Increment(ref _recoveryFrames);
        _decode.Enqueue(session, segments);
    }

    private void OnLevel(object? sender, double level) => Dispatcher.UIThread.Post(() => _state.AudioLevel = level);
    private void OnTranscriptChanged(object? sender, DictationSession session) => Dispatcher.UIThread.Post(() =>
    {
        if (_active?.Id == session.Id) _state.LiveTranscript = session.LiveText;
    });
    private void OnSessionCompleted(object? sender, DictationSession session) => _ = CommitCompletedAsync(session);

    private async Task CommitCompletedAsync(DictationSession session)
    {
        string segmentText = session.SegmentText;
        string? fallback;
        lock (_gate) fallback = _textFallbackPhrases.Remove(session.Id, out string? phrase) ? phrase : null;
        if (session.ControlSpans.Count == 0) segmentText = TranscriptTailCleaner.RemoveAcceptedCommandTail(segmentText, fallback);
        segmentText = WordReplacementEngine.Apply(segmentText, _settings.WordReplacementGroups);
        string text = string.Join(' ', new[] { session.PreviousText, segmentText }
            .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        DateTimeOffset stopped = session.StoppedAt ?? DateTimeOffset.UtcNow;
        if (session.IsContinuation && session.Canceled) text = session.PreviousText;
        var record = new TranscriptRecord(session.LogicalTranscriptId, session.LogicalStartedAt, stopped, session.Status, session.Language, text,
            session.Canceled && !session.IsContinuation, false, false, session.LogicalTranscriptId, stopped,
            session.PriorSegmentCount + (session.Canceled && session.IsContinuation ? 0 : 1),
            session.PriorDurationSeconds + (session.Canceled && session.IsContinuation ? 0 : (stopped - session.StartedAt).TotalSeconds));
        try
        {
            bool hasText = !string.IsNullOrWhiteSpace(record.Text);
            if (hasText) await _history.AddAsync(record).ConfigureAwait(false);
            if (_recoveryCloseTasks.TryRemove(session.Id, out Task? closeTask)) await closeTask.ConfigureAwait(false);
            _recovery.Delete(session.Id);
            if (hasText) await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TranscriptRecord? existing = _state.History.FirstOrDefault(x => x.LogicalId == record.LogicalId);
                if (existing is not null) _state.History.Remove(existing);
                _state.History.Insert(0, record);
                while (_state.History.Count > JsonHistoryStore.Retention) _state.History.RemoveAt(_state.History.Count - 1);
                if (_active is null) _state.LiveTranscript = "";
            });
            bool pasteCanceled;
            lock (_gate)
            {
                pasteCanceled = _canceledPasteSessions.Remove(session.Id);
                if (_pendingPasteSessionId == session.Id) _pendingPasteSessionId = null;
            }
            if (hasText && !session.Canceled && session.PasteRequested && !pasteCanceled)
            {
                _paste.StartReserved(text, () => MarkPastedAsync(record));
                session.Pasted = true;
            }
            else if (session.PasteRequested && _paste.IsActive) _paste.Cancel();
            else if (hasText && !session.Canceled && _settings.CopyOnStop) await _paste.CopyAsync(text).ConfigureAwait(false);
            ShowFeedback(session.Canceled ? "Canceled" : !hasText ? "Nothing recorded" : session.Pasted ? "Pasting…" : "Saved");
            LogSessionCompleted(_logger, session.Id, text.Length, session.FinalizationMilliseconds ?? 0);
        }
        catch (Exception ex) { LogCommitFailed(_logger, ex, session.Id); SetStatus("Could not save the transcript; recovery audio was preserved."); }
        finally
        {
            lock (_gate)
            {
                _sessions.Remove(session.Id);
                ReleaseBackend(session.Backend);
            }
            session.Dispose();
        }
    }

    private Task MarkPastedAsync(TranscriptRecord? record)
    {
        Dispatcher.UIThread.Post(() => _state.TransientFeedback = "Pasted");
        return Task.CompletedTask;
    }

    private void OnPasteStateChanged(object? sender, PasteRequestState state) => Dispatcher.UIThread.Post(() => _state.PasteState = state);

    private async Task RecoverInterruptedAsync()
    {
        IAsrBackend? backend;
        string? vad;
        lock (_gate) { backend = _backend; vad = _vadModelPath; }
        if (backend is null || vad is null) return;
        foreach (string directory in _recovery.Discover())
        {
            try
            {
                RecoveryMetadata? metadata = JsonSerializer.Deserialize<RecoveryMetadata>(await File.ReadAllTextAsync(Path.Combine(directory, "session.json")).ConfigureAwait(false), AtomicJsonFile.Options);
                if (metadata is null) continue;
                TranscriptRecord? continued = string.IsNullOrWhiteSpace(metadata.PreviousText) ? null : new TranscriptRecord(
                    metadata.LogicalTranscriptId ?? metadata.SessionId, metadata.LogicalStartedAt ?? metadata.StartedAt, metadata.StartedAt,
                    DictationStatus.Completed, metadata.Language, metadata.PreviousText, false, false, false,
                    metadata.LogicalTranscriptId ?? metadata.SessionId, metadata.StartedAt, metadata.PriorSegmentCount, metadata.PriorDurationSeconds);
                var session = new DictationSession(metadata.Language, 0, backend, SegmenterFactory(vad), metadata.SessionId, metadata.StartedAt, continued);
                lock (_gate) { _sessions[session.Id] = session; _backendUsers[backend] = _backendUsers.GetValueOrDefault(backend) + 1; }
                byte[] audio = await File.ReadAllBytesAsync(Path.Combine(directory, "audio.pcm")).ConfigureAwait(false);
                const int bytesPerFrame = 640;
                for (int offset = 0; offset < audio.Length; offset += bytesPerFrame)
                {
                    AudioFrame frame = Pcm16Converter.Convert(audio.AsSpan(offset, Math.Min(bytesPerFrame, audio.Length - offset)));
                    _decode.Enqueue(session, session.Accept(frame));
                }
                _decode.Finalize(session, session.Stop(false, false));
                _cues.PlayRecovered(_settings.CueVolume);
                ShowFeedback("Recovered");
            }
            catch (Exception ex) { LogRecoveryFailed(_logger, ex, Path.GetFileName(directory)); }
        }
    }

    private void RetireBackend(IAsrBackend backend)
    {
        if (_backendUsers.GetValueOrDefault(backend) == 0) { _backendUsers.Remove(backend); backend.Dispose(); }
        else _retiredBackends.Add(backend);
    }
    private void ReleaseBackend(IAsrBackend backend)
    {
        int remaining = Math.Max(0, _backendUsers.GetValueOrDefault(backend) - 1);
        _backendUsers[backend] = remaining;
        if (remaining == 0 && _retiredBackends.Remove(backend)) { _backendUsers.Remove(backend); backend.Dispose(); }
    }

    private void SetRecordingState(bool recording, DateTimeOffset? started, string status) => Dispatcher.UIThread.Post(() =>
    {
        _state.IsRecording = recording; _state.RecordingStartedAt = started; _state.StatusMessage = status; if (recording) _state.LiveTranscript = "";
    });
    private void SetStatus(string status) => Dispatcher.UIThread.Post(() => _state.StatusMessage = status);
    private void ShowFeedback(string feedback) => Dispatcher.UIThread.Post(() => _state.TransientFeedback = feedback);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        bool recording;
        lock (_gate) recording = _active is not null;
        if (recording) StopRecording();
        await Task.Yield();
        _audio.FrameReady -= OnAudioFrame; _audio.LevelChanged -= OnLevel; _decode.TranscriptChanged -= OnTranscriptChanged;
        _decode.SessionCompleted -= OnSessionCompleted; _commands.CommandRecognized -= OnVoiceCommand; _paste.StateChanged -= OnPasteStateChanged;
        await _audio.DisposeAsync().ConfigureAwait(false); await _decode.DisposeAsync().ConfigureAwait(false); await _recovery.DisposeAsync().ConfigureAwait(false);
        _commands.Dispose();
        lock (_gate)
        {
            foreach (DictationSession session in _sessions.Values) session.Dispose();
            foreach (IAsrBackend backend in _backendUsers.Keys.ToArray()) backend.Dispose();
            _sessions.Clear(); _backendUsers.Clear(); _retiredBackends.Clear(); _backend = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} started (language={Language}, model={Model}).")]
    private static partial void LogSessionStarted(ILogger logger, string sessionId, string language, string model);
    [LoggerMessage(Level = LogLevel.Information, Message = "Session {SessionId} finalized (chars={Characters}, finalizationMs={FinalizationMs:F1}).")]
    private static partial void LogSessionCompleted(ILogger logger, string sessionId, int characters, double finalizationMs);
    [LoggerMessage(Level = LogLevel.Error, Message = "Session {SessionId} could not be committed; recovery audio retained.")]
    private static partial void LogCommitFailed(ILogger logger, Exception exception, string sessionId);
    [LoggerMessage(Level = LogLevel.Error, Message = "Interrupted session {SessionId} could not be recovered.")]
    private static partial void LogRecoveryFailed(ILogger logger, Exception exception, string sessionId);
    [LoggerMessage(Level = LogLevel.Error, Message = "The configured microphone could not be started.")]
    private static partial void LogMicrophoneFailed(ILogger logger, Exception exception);
    [LoggerMessage(Level = LogLevel.Error, Message = "Custom command {CommandId} failed.")]
    private static partial void LogCustomCommandFailed(ILogger logger, Exception exception, string commandId);
}
