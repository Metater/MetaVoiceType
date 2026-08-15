namespace MetaVoiceType.Transcription;

public sealed record AsrRuntimeStatus(
    string ModelId,
    string ModelDisplayName,
    string Provider,
    string Acceleration,
    string? GpuName,
    string RuntimeVersion,
    string? FallbackReason)
{
    public string CompactLabel => $"{ModelDisplayName} · {Acceleration}";
}

public interface IAsrBackend : IDisposable
{
    AsrRuntimeStatus Status { get; }
    string Transcribe(float[] samples);
}
