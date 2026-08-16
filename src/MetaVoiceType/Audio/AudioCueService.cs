using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MetaVoiceType.Audio;

public sealed class AudioCueService : IAudioCueService
{
    public void PlayAccepted(VoiceCommand command, double volume)
    {
        (double frequency, int duration) = Describe(command);
        Play(frequency, volume, duration);
    }

    internal static (double Frequency, int DurationMilliseconds) Describe(VoiceCommand command) => command switch
    {
        VoiceCommand.StartRecording => (660, 90),
        VoiceCommand.ContinueRecording => (600, 105),
        VoiceCommand.StopRecording => (520, 90),
        VoiceCommand.PasteRecording => (780, 90),
        VoiceCommand.CancelRecording => (360, 110),
        VoiceCommand.CancelPaste => (320, 120),
        VoiceCommand.CopyRecordingToClipboard => (880, 75),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    public void PlayError(double volume) => Play(220, volume, 120);
    public void PlayRecovered(double volume) => Play(740, volume, 150);
    internal static double GainForVolume(double volume) => Math.Clamp(volume, 0, 1) * 0.16;

    private static void Play(double frequency, double volume, int milliseconds)
    {
        var signal = new SignalGenerator(16000, 1) { Type = SignalGeneratorType.Sin, Frequency = frequency, Gain = GainForVolume(volume) };
        var output = new WaveOutEvent();
        output.Init(signal.Take(TimeSpan.FromMilliseconds(milliseconds)));
        output.PlaybackStopped += (_, _) => output.Dispose();
        output.Play();
    }
}
