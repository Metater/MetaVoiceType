using System.Diagnostics;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Streams a WAV file with real-time pacing (frames are yielded at the file's
/// natural rate) so queue dynamics match microphone capture. Supports
/// repetition to simulate long recordings.
/// </summary>
public sealed class WavFileSource : IAudioSource
{
    private readonly string _path;
    private readonly int _chunkMs;
    private readonly int _repeat;
    private readonly bool _paced;
    private readonly ILogger _log;

    public int SampleRate { get; private set; }

    public WavFileSource(string path, int chunkMs, int repeat, ILogger log, bool paced = true)
    {
        _path = path;
        _chunkMs = chunkMs;
        _repeat = Math.Max(1, repeat);
        _paced = paced;
        _log = log;
        SampleRate = 16000;
    }

    public async IAsyncEnumerable<float[]> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
        if (bytes.Length < 44 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF")
            throw new InvalidDataException($"{_path} is not a RIFF/WAV file.");

        int sampleRate = BitConverter.ToInt32(bytes, 24);
        short channels = BitConverter.ToInt16(bytes, 22);
        int dataOffset = 12;
        while (dataOffset + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, dataOffset, 4);
            int size = BitConverter.ToInt32(bytes, dataOffset + 4);
            if (id == "data")
            {
                dataOffset += 8;
                break;
            }
            dataOffset += 8 + size + (size & 1);
        }
        int pcmBytes = bytes.Length - dataOffset;
        short[] pcm16 = new short[pcmBytes / 2];
        Buffer.BlockCopy(bytes, dataOffset, pcm16, 0, pcmBytes);

        SampleRate = sampleRate;
        _log.LogInformation("WAV {Path}: {Rate} Hz, {Channels} ch, {DurationSec:F1}s (paced={Paced})",
            _path, sampleRate, channels, pcm16.Length / (double)sampleRate, _paced);

        int chunkSamples = Math.Max(sampleRate * _chunkMs / 1000, 1);
        var pace = Stopwatch.StartNew();

        for (int repeat = 0; repeat < _repeat && !ct.IsCancellationRequested; repeat++)
        {
            for (int offset = 0; offset < pcm16.Length && !ct.IsCancellationRequested; offset += chunkSamples)
            {
                int n = Math.Min(chunkSamples, pcm16.Length - offset);
                float[] frame = new float[n];
                for (int i = 0; i < n; i++)
                    frame[i] = pcm16[offset + i] / 32768f;

                if (_paced)
                {
                    // Real-time pacing: each frame is yielded at its natural
                    // playback time.
                    double targetMs = (offset + n) / (double)sampleRate * 1000.0;
                    double elapsedMs = pace.Elapsed.TotalMilliseconds;
                    double waitMs = targetMs - elapsedMs;
                    if (waitMs > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(waitMs), ct).ConfigureAwait(false);
                }

                yield return frame;
            }
            _log.LogInformation("WAV {Path}: finished pass {Pass}/{Total} ({Samples} samples).",
                _path, repeat + 1, _repeat, pcm16.Length);
            if (_repeat > 1)
                pace.Restart();
        }
    }

    public void Dispose() { }
}
