using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// NAudio WASAPI microphone capture. The DataAvailable callback only converts
/// and enqueues into a bounded channel; all downstream work (ASR, recovery
/// writes, Vosk) happens off the callback.
///
/// Frames are plain arrays (~640 floats per 40ms callback ≈ 64 KB/s). Pooling
/// was deliberately NOT used because downstream consumers (ASR feed, recovery
/// writer, later Vosk) all need the same frame with independent lifetimes —
/// pooled buffers would make ownership error-prone. Gen0 handles this
/// allocation rate trivially; revisit only if profiling says otherwise.
/// </summary>
public sealed class NaudioCaptureSource : IAudioSource
{
    private const int TargetSampleRate = 16000;
    private const int QueueCapacity = 64;

    private readonly int _deviceIndex;
    private readonly ILogger _log;
    private readonly Channel<float[]> _channel;
    private WasapiCapture? _capture;
    private int _droppedFrames;
    private int _callbackStalls;
    private long _maxObservedDepth;
    private int _disposed;

    public int SampleRate => TargetSampleRate;
    public long MaxObservedDepth => Interlocked.Read(ref _maxObservedDepth);
    public int DroppedFrames => Volatile.Read(ref _droppedFrames);
    public int CallbackStalls => Volatile.Read(ref _callbackStalls);

    public NaudioCaptureSource(int deviceIndex, ILogger log)
    {
        _deviceIndex = deviceIndex;
        _log = log;
        // Bounded; normal operation runs far below capacity. FullMode.Wait
        // means extreme backpressure stalls the callback briefly instead of
        // silently discarding dictation audio.
        _channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(QueueCapacity)
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

        MMDevice device;
        using (var enumerator = new MMDeviceEnumerator())
        {
            if (_deviceIndex >= 0)
            {
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
                if (_deviceIndex >= devices.Count)
                    throw new InvalidOperationException(
                        $"NAudio capture device index {_deviceIndex} out of range ({devices.Count} devices).");
                device = devices[_deviceIndex];
            }
            else
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
        }

        _log.LogInformation("NAudio capture using device: {Name}", device.FriendlyName);

        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 40)
        {
            WaveFormat = new WaveFormat(TargetSampleRate, 16, 1)
        };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                _log.LogError(e.Exception, "NAudio capture stopped with error.");
        };
        _capture.StartRecording();
        _log.LogInformation("NAudio WASAPI capture started at {Rate} Hz mono PCM16.", TargetSampleRate);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        // NAudio reuses its internal buffer; copy out immediately.
        int sampleCount = e.BytesRecorded / 2;
        var frame = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            frame[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;

        if (!_channel.Writer.TryWrite(frame))
        {
            // Queue full: block briefly rather than discard speech.
            // Only triggers if the consumer is seconds behind.
            Interlocked.Increment(ref _callbackStalls);
            if (_channel.Writer.WaitToWriteAsync().AsTask().GetAwaiter().GetResult())
            {
                if (!_channel.Writer.TryWrite(frame))
                    Interlocked.Increment(ref _droppedFrames);
            }
            else
            {
                Interlocked.Increment(ref _droppedFrames);
            }
        }

        UpdateDepthMetric();
    }

    private void UpdateDepthMetric()
    {
        int depth = _channel.Reader.Count;
        long max = Interlocked.Read(ref _maxObservedDepth);
        while (depth > max)
        {
            long current = Interlocked.CompareExchange(ref _maxObservedDepth, depth, max);
            if (current == max)
                break;
            max = current;
        }
    }

    public int DrainDroppedFrameCount()
    {
        int d = Interlocked.Exchange(ref _droppedFrames, 0);
        if (d > 0)
            _log.LogError("NAudio DROPPED {Count} audio frames — dictation audio was lost!", d);
        return d;
    }

    private void Stop()
    {
        WasapiCapture? capture = Interlocked.Exchange(ref _capture, null);
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.StopRecording();
            capture.Dispose();
            _log.LogInformation("NAudio capture stopped. Max queue depth: {Depth}, stalls: {Stalls}.",
                MaxObservedDepth, CallbackStalls);
        }
        _channel.Writer.TryComplete();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Stop();
    }
}
