using SherpaOnnx;

namespace MetaVoiceType.Transcription;

public sealed record SpeechAudioSegment(long StartSample, float[] Samples)
{
    public long EndSample => StartSample + Samples.LongLength;
}

public interface ISpeechSegmenter : IDisposable
{
    IReadOnlyList<SpeechAudioSegment> Accept(ReadOnlySpan<float> samples);
    IReadOnlyList<SpeechAudioSegment> Flush();
}

public sealed class SherpaVadSegmenter : ISpeechSegmenter
{
    public const int WindowSize = 512;
    public const float SpeechThreshold = 0.25f;
    public const float MinimumSilenceSeconds = 0.30f;
    public const float MinimumSpeechSeconds = 0.15f;
    public const float MaximumSpeechSeconds = 10f;
    public static double TailClosureBudgetMilliseconds => Math.Ceiling(MinimumSilenceSeconds * 16000 / WindowSize) * WindowSize / 16d;
    private readonly VoiceActivityDetector _vad;
    private readonly float[] _window = new float[WindowSize];
    private int _windowCount;

    public SherpaVadSegmenter(string modelPath)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        config.SileroVad.Threshold = SpeechThreshold;
        config.SileroVad.MinSilenceDuration = MinimumSilenceSeconds;
        config.SileroVad.MinSpeechDuration = MinimumSpeechSeconds;
        config.SileroVad.MaxSpeechDuration = MaximumSpeechSeconds;
        config.SileroVad.WindowSize = WindowSize;
        config.SampleRate = 16000;
        config.NumThreads = 1;
        config.Provider = "cpu";
        config.Debug = 0;
        _vad = new VoiceActivityDetector(config, 120);
    }

    public IReadOnlyList<SpeechAudioSegment> Accept(ReadOnlySpan<float> samples)
    {
        var completed = new List<SpeechAudioSegment>();
        while (!samples.IsEmpty)
        {
            int count = Math.Min(WindowSize - _windowCount, samples.Length);
            samples[..count].CopyTo(_window.AsSpan(_windowCount));
            _windowCount += count;
            samples = samples[count..];
            if (_windowCount != WindowSize) continue;
            _vad.AcceptWaveform(_window);
            _windowCount = 0;
            Drain(completed);
        }
        return completed;
    }

    public IReadOnlyList<SpeechAudioSegment> Flush()
    {
        var completed = new List<SpeechAudioSegment>();
        if (_windowCount > 0)
        {
            Array.Clear(_window, _windowCount, WindowSize - _windowCount);
            _vad.AcceptWaveform(_window);
            _windowCount = 0;
        }
        _vad.Flush();
        Drain(completed);
        return completed;
    }

    private void Drain(List<SpeechAudioSegment> output)
    {
        while (!_vad.IsEmpty())
        {
            SpeechSegment segment = _vad.Front();
            output.Add(new(segment.Start, segment.Samples));
            _vad.Pop();
        }
    }

    public void Dispose() => _vad.Dispose();
}
