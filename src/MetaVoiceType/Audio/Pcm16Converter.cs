namespace MetaVoiceType.Audio;

public static class Pcm16Converter
{
    public static AudioFrame Convert(ReadOnlySpan<byte> pcm16)
    {
        int count = pcm16.Length / 2;
        var samples = new float[count];
        var bytes = pcm16[..(count * 2)].ToArray();
        for (int i = 0; i < count; i++) samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
        return new(samples, bytes, DateTimeOffset.UtcNow);
    }

    public static byte[] ToPcm16(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short value = (short)Math.Clamp((int)Math.Round(samples[i] * 32767), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), value);
        }
        return bytes;
    }
}
