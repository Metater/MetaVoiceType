using MetaVoiceType.Core.Interfaces;
using MetaVoiceType.Core.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MetaVoiceType.Audio;

public sealed class AudioCueService : IAudioCueService
{
    public void PlayAccepted(VoiceCommand command, double volume) => Play(command switch
    {
        VoiceCommand.StartRecording => 660,
        VoiceCommand.StopRecording => 520,
        VoiceCommand.PasteHere => 780,
        VoiceCommand.CancelRecording => 360,
        VoiceCommand.CancelPaste => 320,
        VoiceCommand.CopyRecordingToClipboard => 880,
        _ => 600
    }, volume, 90);

    public void PlayError(double volume) => Play(220, volume, 120);
    public void PlayRecovered(double volume) => Play(740, volume, 150);

    private static void Play(double frequency, double volume, int milliseconds)
    {
        var signal = new SignalGenerator(16000, 1) { Type = SignalGeneratorType.Sin, Frequency = frequency, Gain = Math.Clamp(volume, 0, 1) * 0.16 };
        var output = new WaveOutEvent();
        output.Init(signal.Take(TimeSpan.FromMilliseconds(milliseconds)));
        output.PlaybackStopped += (_, _) => output.Dispose();
        output.Play();
    }
}
