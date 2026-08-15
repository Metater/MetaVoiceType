using System.Runtime.InteropServices;
using System.Threading.Channels;
using PortAudioSharp;

namespace MetaVoiceType.ConsolePrototype;

public interface IAudioSource : IDisposable
{
    int SampleRate { get; }
    IAsyncEnumerable<float[]> ReadFramesAsync(CancellationToken ct);
}

/// <summary>
/// PortAudio microphone capture. The native callback only copies samples into a
/// bounded channel; all transcription work happens off the callback thread.
/// </summary>
public sealed class MicrophoneAudioSource : IAudioSource
{
    private readonly int _deviceIndex;
    private readonly ILogger _log;
    private readonly Channel<float[]> _channel;
    private readonly double _sampleRate = 16000;
    private PortAudioSharp.Stream? _stream;
    private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile int _droppedFrames;

    public int SampleRate => (int)_sampleRate;

    public MicrophoneAudioSource(int deviceIndex, ILogger log)
    {
        _deviceIndex = deviceIndex;
        _log = log;
        _channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
    }

    public async IAsyncEnumerable<float[]> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        Start();
        try
        {
            await foreach (float[] frame in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return frame;
        }
        finally
        {
            Stop();
        }
    }

    private void Start()
    {
        PortAudio.Initialize();
        int deviceIndex = _deviceIndex;
        if (deviceIndex < 0)
        {
            deviceIndex = PortAudio.DefaultInputDevice;
            if (deviceIndex == PortAudio.NoDevice)
                throw new InvalidOperationException("No default input device found.");
        }
        DeviceInfo info = PortAudio.GetDeviceInfo(deviceIndex);
        _log.LogInformation("Using input device {Index}: {Name}", deviceIndex, info.name);

        var param = new StreamParameters
        {
            device = deviceIndex,
            channelCount = 1,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        _stream = new PortAudioSharp.Stream(
            inParams: param,
            outParams: null,
            sampleRate: _sampleRate,
            framesPerBuffer: 0,
            streamFlags: StreamFlags.ClipOff,
            callback: Callback,
            userData: IntPtr.Zero);

        _stream.Start();
        _started.TrySetResult();
        _log.LogInformation("Microphone capture started at {Rate} Hz.", _sampleRate);
    }

    private StreamCallbackResult Callback(
        IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        if (input == IntPtr.Zero)
            return StreamCallbackResult.Continue;

        var samples = new float[frameCount];
        Marshal.Copy(input, samples, 0, (int)frameCount);

        if (!_channel.Writer.TryWrite(samples))
            Interlocked.Increment(ref _droppedFrames);
        return StreamCallbackResult.Continue;
    }

    public int DrainDroppedFrameCount()
    {
        int d = Interlocked.Exchange(ref _droppedFrames, 0);
        if (d > 0)
            _log.LogWarning("Dropped {Count} audio frames (consumer fell behind).", d);
        return d;
    }

    private void Stop()
    {
        _channel.Writer.TryComplete();
        _stream?.Stop();
        _stream?.Dispose();
        _stream = null;
        PortAudio.Terminate();
        _log.LogInformation("Microphone capture stopped.");
    }

    public void Dispose() => Stop();
}
