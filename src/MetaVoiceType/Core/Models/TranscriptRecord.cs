namespace MetaVoiceType.Core.Models;

public enum DictationStatus { Recording, Finalizing, Completed, Canceled, Recoverable, Faulted }

public sealed record TranscriptRecord(
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset StoppedAt,
    DictationStatus Status,
    string Language,
    string Text,
    bool Canceled,
    bool Copied,
    bool Pasted);
