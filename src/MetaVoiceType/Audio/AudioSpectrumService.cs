using MetaVoiceType.Core.Interfaces;

namespace MetaVoiceType.Audio;

public sealed class AudioSpectrumService : IAsyncDisposable
{
    public const int BarCount = 20;
    private const int FftSize = 2048;
    private const double NoiseFloorDb = -75;
    private const double CeilingDb = -20;
    private readonly IAudioCaptureService _audio;
    private readonly object _gate = new();
    private readonly double[] _ring = new double[FftSize];
    private readonly double[] _smoothed = new double[BarCount];
    private readonly FftSharp.Windows.Hanning _window = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private int _writePosition;
    private int _sampleCount;
    private long _version;
    private long _publishedVersion;
    private int _consumerCount;

    public AudioSpectrumService(IAudioCaptureService audio)
    {
        _audio = audio;
        _audio.FrameReady += OnAudioFrame;
        _worker = RunAsync(_shutdown.Token);
    }

    public IReadOnlyList<double> CurrentFrame { get; private set; } = new double[BarCount];
    public event EventHandler<IReadOnlyList<double>>? FrameReady;
    public IDisposable Acquire()
    {
        Interlocked.Increment(ref _consumerCount);
        return new SpectrumLease(this);
    }

    private void OnAudioFrame(object? sender, AudioFrame frame)
    {
        lock (_gate)
        {
            foreach (float sample in frame.Samples)
            {
                _ring[_writePosition] = sample;
                _writePosition = (_writePosition + 1) % FftSize;
                _sampleCount = Math.Min(FftSize, _sampleCount + 1);
            }
            _version++;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000d / 30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) PublishFrame();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void PublishFrame()
    {
        var samples = new double[FftSize];
        lock (_gate)
        {
            if (Volatile.Read(ref _consumerCount) == 0 || _sampleCount < FftSize || _version == _publishedVersion) return;
            _publishedVersion = _version;
            int first = _writePosition;
            int tail = FftSize - first;
            Array.Copy(_ring, first, samples, 0, tail);
            Array.Copy(_ring, 0, samples, tail, first);
        }

        _window.ApplyInPlace(samples);
        double[] power = FftSharp.FFT.Power(FftSharp.FFT.Forward(samples));
        var frame = new double[BarCount];
        const double minFrequency = 80;
        const double maxFrequency = 4000;
        for (int bar = 0; bar < BarCount; bar++)
        {
            double low = minFrequency * Math.Pow(maxFrequency / minFrequency, bar / (double)BarCount);
            double high = minFrequency * Math.Pow(maxFrequency / minFrequency, (bar + 1d) / BarCount);
            int lowBin = Math.Clamp((int)Math.Floor(low * FftSize / AudioFrame.SampleRate), 1, power.Length - 1);
            int highBin = Math.Clamp((int)Math.Ceiling(high * FftSize / AudioFrame.SampleRate), lowBin + 1, power.Length);
            double peak = double.NegativeInfinity;
            for (int bin = lowBin; bin < highBin; bin++) peak = Math.Max(peak, power[bin]);
            double normalized = Math.Clamp((peak - NoiseFloorDb) / (CeilingDb - NoiseFloorDb), 0, 1);
            double smoothing = normalized > _smoothed[bar] ? 0.55 : 0.18;
            _smoothed[bar] += (normalized - _smoothed[bar]) * smoothing;
            frame[bar] = _smoothed[bar];
        }
        CurrentFrame = frame;
        FrameReady?.Invoke(this, frame);
    }

    public async ValueTask DisposeAsync()
    {
        _audio.FrameReady -= OnAudioFrame;
        _shutdown.Cancel();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }

    private sealed class SpectrumLease(AudioSpectrumService owner) : IDisposable
    {
        private AudioSpectrumService? _owner = owner;
        public void Dispose()
        {
            AudioSpectrumService? current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) Interlocked.Decrement(ref current._consumerCount);
        }
    }
}
