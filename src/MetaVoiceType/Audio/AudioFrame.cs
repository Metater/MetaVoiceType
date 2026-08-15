namespace MetaVoiceType.Audio;

public sealed record AudioFrame(float[] Samples, byte[] Pcm16, DateTimeOffset CapturedAt)
{
    public const int SampleRate = 16000;
    public double Peak => Samples.Length == 0 ? 0 : Samples.Max(Math.Abs);
}
