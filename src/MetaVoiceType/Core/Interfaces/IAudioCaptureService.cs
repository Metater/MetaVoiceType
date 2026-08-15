using MetaVoiceType.Audio;

namespace MetaVoiceType.Core.Interfaces;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);
public sealed record AudioMetrics(long FramesCaptured, int QueueDepth, int MaxQueueDepth, long LostFrames, double CallbackMilliseconds);

public interface IAudioCaptureService : IAsyncDisposable
{
    event EventHandler<AudioFrame>? FrameReady;
    event EventHandler<double>? LevelChanged;
    bool IsRunning { get; }
    AudioMetrics Metrics { get; }
    IReadOnlyList<AudioDevice> EnumerateDevices();
    Task StartAsync(string? deviceId, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
