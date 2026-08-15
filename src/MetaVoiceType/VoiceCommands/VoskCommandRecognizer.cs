using System.Text.Json;
using System.Text.Encodings.Web;
using MetaVoiceType.Audio;
using MetaVoiceType.Core.Models;
using Microsoft.Extensions.Logging;
using Vosk;

namespace MetaVoiceType.VoiceCommands;

public sealed partial class VoskCommandRecognizer(ILogger<VoskCommandRecognizer> logger) : IDisposable
{
    private static readonly JsonSerializerOptions GrammarJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object _gate = new();
    private Model? _model;
    private VoskRecognizer? _recognizer;
    private IReadOnlyDictionary<VoiceCommand, string> _phrases = new Dictionary<VoiceCommand, string>();
    private DateTimeOffset _lastAccepted;
    private int _disposed;
    public bool IsReady { get { lock (_gate) return _recognizer is not null; } }
    public event EventHandler<VoiceCommandMatch>? CommandRecognized;

    public void Load(string modelPath, IReadOnlyDictionary<VoiceCommand, string> phrases, bool restrictedGrammar = true)
    {
        CommandPhraseValidator.Validate(phrases.ToDictionary(x => VoiceCommandKeys.All[x.Key], x => x.Value));
        bool useRestrictedGrammar = restrictedGrammar && SupportsManagedGrammar(phrases);
        var replacementModel = new Model(modelPath);
        VoskRecognizer replacementRecognizer;
        try { replacementRecognizer = Create(replacementModel, phrases, useRestrictedGrammar); }
        catch { replacementModel.Dispose(); throw; }
        lock (_gate)
        {
            VoskRecognizer? previousRecognizer = _recognizer;
            Model? previousModel = _model;
            _model = replacementModel;
            _phrases = new Dictionary<VoiceCommand, string>(phrases);
            _recognizer = replacementRecognizer;
            previousRecognizer?.Dispose(); previousModel?.Dispose();
        }
        if (restrictedGrammar && !useRestrictedGrammar) LogUnicodeFallback(logger);
        LogLoaded(logger, modelPath, useRestrictedGrammar);
    }

    public void RebuildGrammar(IReadOnlyDictionary<VoiceCommand, string> phrases, bool restrictedGrammar = true)
    {
        CommandPhraseValidator.Validate(phrases.ToDictionary(x => VoiceCommandKeys.All[x.Key], x => x.Value));
        lock (_gate)
        {
            if (_model is null) throw new InvalidOperationException("Vosk model is not loaded.");
            bool useRestrictedGrammar = restrictedGrammar && SupportsManagedGrammar(phrases);
            VoskRecognizer replacement = Create(_model, phrases, useRestrictedGrammar);
            _recognizer?.Dispose(); _recognizer = replacement; _phrases = new Dictionary<VoiceCommand, string>(phrases);
            if (restrictedGrammar && !useRestrictedGrammar) LogUnicodeFallback(logger);
        }
    }

    private static VoskRecognizer Create(Model model, IReadOnlyDictionary<VoiceCommand, string> phrases, bool restricted)
    {
        string grammar = JsonSerializer.Serialize(phrases.Values.Select(CommandPhraseValidator.Normalize).Append("[unk]").Distinct(), GrammarJsonOptions);
        var recognizer = restricted ? new VoskRecognizer(model, AudioFrame.SampleRate, grammar) : new VoskRecognizer(model, AudioFrame.SampleRate);
        recognizer.SetMaxAlternatives(3);
        recognizer.SetWords(true);
        return recognizer;
    }

    internal static bool SupportsManagedGrammar(IReadOnlyDictionary<VoiceCommand, string> phrases) =>
        phrases.Values.All(value => value.All(character => character <= 0x7f));

    public void Accept(AudioFrame frame)
    {
        IReadOnlyList<VoiceCommandMatch> matches = [];
        lock (_gate)
        {
            if (_recognizer is null || !_recognizer.AcceptWaveform(frame.Pcm16, frame.Pcm16.Length)) return;
            matches = VoskResultMatcher.Match(_recognizer.Result(), _phrases);
        }
        foreach (VoiceCommandMatch match in matches)
        {
            if (DateTimeOffset.UtcNow - _lastAccepted < TimeSpan.FromMilliseconds(700)) continue;
            _lastAccepted = DateTimeOffset.UtcNow;
            LogAccepted(logger, match.Command);
            CommandRecognized?.Invoke(this, match);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        lock (_gate) { _recognizer?.Dispose(); _model?.Dispose(); _recognizer = null; _model = null; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Vosk model {Model} loaded (restricted grammar={Restricted}).")]
    private static partial void LogLoaded(ILogger logger, string model, bool restricted);
    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted voice command {Command}.")]
    private static partial void LogAccepted(ILogger logger, VoiceCommand command);
    [LoggerMessage(Level = LogLevel.Warning, Message = "The official Vosk C# binding cannot marshal non-ASCII runtime grammar safely on Windows; using unrestricted recognition with exact configured-phrase matching.")]
    private static partial void LogUnicodeFallback(ILogger logger);
}
