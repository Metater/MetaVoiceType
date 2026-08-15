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
    private long _maxObservedDepth;

    public int SampleRate => (int)_sampleRate;

    /// <summary>Peak number of queued frames observed (diagnostics).</summary>
    public long MaxObservedDepth => Interlocked.Read(ref _maxObservedDepth);

    public MicrophoneAudioSource(int deviceIndex, ILogger log)
    {
        _deviceIndex = deviceIndex;
        _log = log;
        // Bounded to bound memory; FullMode.Wait means we prefer blocking the
        // capture thread briefly over silently discarding dictation audio.
        _channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
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

        // FullMode.Wait: blocks the capture thread when the queue is full
        // rather than discarding dictation audio. Under normal operation the
        // consumer stays far ahead, so this never blocks.
        if (!_channel.Writer.TryWrite(samples))
        {
            if (_channel.Writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult())
            {
                if (!_channel.Writer.TryWrite(samples))
                    Interlocked.Increment(ref _droppedFrames);
            }
            else
            {
                Interlocked.Increment(ref _droppedFrames);
            }
        }

        long depth = _channel.Reader.Count;
        long max = Interlocked.Read(ref _maxObservedDepth);
        while (depth > max)
        {
            long current = Interlocked.CompareExchange(ref _maxObservedDepth, depth, max);
            if (current == max)
                break;
            max = current;
        }
        return StreamCallbackResult.Continue;
    }

    public int DrainDroppedFrameCount()
    {
        int d = Interlocked.Exchange(ref _droppedFrames, 0);
        if (d > 0)
            _log.LogError("DROPPED {Count} audio frames — dictation audio was lost!", d);
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
