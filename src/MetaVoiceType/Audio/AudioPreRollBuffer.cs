namespace MetaVoiceType.Audio;

public sealed record PositionedAudioFrame(long StartSample, AudioFrame Frame)
{
    public long EndSample => StartSample + Frame.Samples.LongLength;
}

public sealed class AudioPreRollBuffer(int capacitySamples = AudioFrame.SampleRate)
{
    private readonly object _gate = new();
    private readonly Queue<PositionedAudioFrame> _frames = new();
    private readonly int _capacitySamples = Math.Max(1, capacitySamples);

    public void Add(long startSample, AudioFrame frame)
    {
        lock (_gate)
        {
            _frames.Enqueue(new(startSample, frame));
            long cutoff = startSample + frame.Samples.LongLength - _capacitySamples;
            while (_frames.TryPeek(out PositionedAudioFrame? oldest) && oldest.EndSample <= cutoff) _frames.Dequeue();
        }
    }

    public IReadOnlyList<AudioFrame> Snapshot(long afterSample, long throughSample)
    {
        lock (_gate)
        {
            var result = new List<AudioFrame>();
            foreach (PositionedAudioFrame positioned in _frames)
            {
                long start = Math.Max(positioned.StartSample, afterSample);
                long end = Math.Min(positioned.EndSample, throughSample);
                if (end <= start) continue;
                int offset = checked((int)(start - positioned.StartSample));
                int length = checked((int)(end - start));
                float[] samples = positioned.Frame.Samples.AsSpan(offset, length).ToArray();
                byte[] pcm = Pcm16Converter.ToPcm16(samples);
                result.Add(new(samples, pcm, positioned.Frame.CapturedAt));
            }
            return result;
        }
    }
}
