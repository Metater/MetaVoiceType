using MetaVoiceType.Audio;

namespace MetaVoiceType.Core.Interfaces;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);
public sealed record AudioMetrics(long FramesCaptured, int QueueDepth, int MaxQueueDepth, long LostFrames, double CallbackMilliseconds, long FramesDispatched = 0)
{
    public long FramesDropped => LostFrames;
    public int CaptureQueueHighWaterMark => MaxQueueDepth;
}

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
