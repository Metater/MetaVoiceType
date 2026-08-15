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
    private const int WindowSize = 512;
    private readonly VoiceActivityDetector _vad;
    private readonly float[] _window = new float[WindowSize];
    private int _windowCount;

    public SherpaVadSegmenter(string modelPath)
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = modelPath;
        config.SileroVad.Threshold = 0.3f;
        config.SileroVad.MinSilenceDuration = 0.45f;
        config.SileroVad.MinSpeechDuration = 0.2f;
        config.SileroVad.MaxSpeechDuration = 20f;
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
