using System.Text.Encodings.Web;
using System.Text.Json;
using MetaVoiceType.Audio;
using MetaVoiceType.Core.Models;
using Microsoft.Extensions.Logging;
using Vosk;

namespace MetaVoiceType.VoiceCommands;

public sealed partial class VoskCommandRecognizer(ILogger<VoskCommandRecognizer> logger) : IDisposable
{
    private static readonly JsonSerializerOptions GrammarJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAccepted = new(StringComparer.Ordinal);
    private Model? _model;
    private VoskRecognizer? _recognizer;
    private IReadOnlyList<VoiceCommandDefinition> _definitions = [];
    private long _globalSamples;
    private long _recognizerBaseSample;
    private int _disposed;
    public bool IsReady { get { lock (_gate) return _recognizer is not null; } }
    public event EventHandler<VoiceCommandMatch>? CommandRecognized;

    public void Load(string modelPath, IReadOnlyDictionary<VoiceCommand, string> phrases, bool restrictedGrammar = true) =>
        Load(modelPath, phrases.Select(x => VoiceCommandDefinition.BuiltIn(x.Key, x.Value)).ToArray(), restrictedGrammar);

    public void Load(string modelPath, IReadOnlyList<VoiceCommandDefinition> definitions, bool restrictedGrammar = true)
    {
        Validate(definitions);
        bool useRestrictedGrammar = restrictedGrammar && SupportsManagedGrammar(definitions);
        var replacementModel = new Model(modelPath);
        VoskRecognizer replacementRecognizer;
        try { replacementRecognizer = Create(replacementModel, definitions, useRestrictedGrammar); }
        catch { replacementModel.Dispose(); throw; }
        lock (_gate)
        {
            VoskRecognizer? previousRecognizer = _recognizer;
            Model? previousModel = _model;
            _model = replacementModel;
            _definitions = definitions.ToArray();
            _recognizer = replacementRecognizer;
            _recognizerBaseSample = _globalSamples;
            previousRecognizer?.Dispose(); previousModel?.Dispose();
        }
        if (restrictedGrammar && !useRestrictedGrammar) LogUnicodeFallback(logger);
        LogLoaded(logger, modelPath, useRestrictedGrammar);
    }

    public void RebuildGrammar(IReadOnlyDictionary<VoiceCommand, string> phrases, bool restrictedGrammar = true) =>
        RebuildGrammar(phrases.Select(x => VoiceCommandDefinition.BuiltIn(x.Key, x.Value)).ToArray(), restrictedGrammar);

    public void RebuildGrammar(IReadOnlyList<VoiceCommandDefinition> definitions, bool restrictedGrammar = true)
    {
        Validate(definitions);
        lock (_gate)
        {
            if (_model is null) throw new InvalidOperationException("Vosk model is not loaded.");
            bool useRestrictedGrammar = restrictedGrammar && SupportsManagedGrammar(definitions);
            VoskRecognizer replacement = Create(_model, definitions, useRestrictedGrammar);
            _recognizer?.Dispose();
            _recognizer = replacement;
            _definitions = definitions.ToArray();
            _recognizerBaseSample = _globalSamples;
            if (restrictedGrammar && !useRestrictedGrammar) LogUnicodeFallback(logger);
        }
    }

    private static VoskRecognizer Create(Model model, IReadOnlyList<VoiceCommandDefinition> definitions, bool restricted)
    {
        string grammar = JsonSerializer.Serialize(definitions.Select(x => CommandPhraseValidator.Normalize(x.Phrase)).Append("[unk]").Distinct(), GrammarJsonOptions);
        var recognizer = restricted ? new VoskRecognizer(model, AudioFrame.SampleRate, grammar) : new VoskRecognizer(model, AudioFrame.SampleRate);
        recognizer.SetMaxAlternatives(3);
        recognizer.SetWords(true);
        return recognizer;
    }

    private static void Validate(IReadOnlyList<VoiceCommandDefinition> definitions)
    {
        if (definitions.Count == 0) throw new InvalidDataException("At least one voice command is required.");
        CommandPhraseValidator.Validate(definitions.ToDictionary(x => x.Id, x => x.Phrase, StringComparer.Ordinal));
    }

    internal static bool SupportsManagedGrammar(IReadOnlyDictionary<VoiceCommand, string> phrases) => phrases.Values.All(IsAscii);
    internal static bool SupportsManagedGrammar(IReadOnlyList<VoiceCommandDefinition> definitions) => definitions.All(x => IsAscii(x.Phrase));
    private static bool IsAscii(string value) => value.All(character => character <= 0x7f);

    public void Accept(AudioFrame frame)
    {
        IReadOnlyList<VoiceCommandMatch> matches = [];
        lock (_gate)
        {
            long baseSample = _recognizerBaseSample;
            _globalSamples += frame.Samples.LongLength;
            if (_recognizer is null || !_recognizer.AcceptWaveform(frame.Pcm16, frame.Pcm16.Length)) return;
            matches = VoskResultMatcher.Match(_recognizer.Result(), _definitions, baseSample);
        }
        foreach (VoiceCommandMatch match in matches)
        {
            lock (_gate)
            {
                if (_lastAccepted.TryGetValue(match.CommandId, out DateTimeOffset last) && match.AcceptedAt - last < TimeSpan.FromMilliseconds(650)) continue;
                _lastAccepted[match.CommandId] = match.AcceptedAt;
            }
            LogAccepted(logger, match.CommandId, match.AudioStartSample, match.AudioEndSample);
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
    [LoggerMessage(Level = LogLevel.Information, Message = "Accepted voice command {CommandId} at samples {StartSample}..{EndSample}.")]
    private static partial void LogAccepted(ILogger logger, string commandId, long? startSample, long? endSample);
    [LoggerMessage(Level = LogLevel.Warning, Message = "The official Vosk C# binding cannot marshal non-ASCII runtime grammar safely on Windows; using unrestricted recognition with exact configured-phrase matching.")]
    private static partial void LogUnicodeFallback(ILogger logger);
}
