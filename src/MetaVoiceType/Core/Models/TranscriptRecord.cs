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
    bool Pasted,
    string? LogicalTranscriptId = null,
    DateTimeOffset? UpdatedAt = null,
    int SegmentCount = 1,
    double TotalDurationSeconds = 0)
{
    public string LogicalId => string.IsNullOrWhiteSpace(LogicalTranscriptId) ? SessionId : LogicalTranscriptId;
    public DateTimeOffset StartedAtUtc => StartedAt.ToUniversalTime();
    public DateTimeOffset StoppedAtUtc => StoppedAt.ToUniversalTime();
    public DateTimeOffset? UpdatedAtUtc => UpdatedAt?.ToUniversalTime();
    public string LocalTimeDisplay => MetaVoiceType.Storage.TranscriptTimeFormatter.Format(StartedAtUtc);
}
