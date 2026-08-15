using System.Diagnostics;
using SherpaOnnx;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Phase 2 preview: proves that a finalizing session and a live session can
/// share one recognizer. FinalizeBlocking drains session A; session B keeps
/// accepting audio into its own stream, and decode continues afterward.
/// </summary>
public static class ConcurrencyProbe
{
    public static async Task RunAsync(Options opts, ILogger log, CancellationToken ct)
    {
        var cfg = new OnlineRecognizerConfig
        {
            FeatConfig = { SampleRate = 16000, FeatureDim = 80 },
            ModelConfig =
            {
                Transducer =
                {
                    Encoder = opts.Encoder,
                    Decoder = opts.Decoder,
                    Joiner = opts.Joiner
                },
                Tokens = opts.Tokens,
                Provider = opts.Provider,
                NumThreads = opts.NumThreads,
                Debug = 0
            },
            DecodingMethod = opts.DecodingMethod,
            MaxActivePaths = opts.MaxActivePaths
        };

        using var engine = new TranscriptionEngine(cfg, log);
        using var a = engine.CreateSession("A", opts.Language);
        using var b = engine.CreateSession("B", opts.Language);

        string wavPath = opts.WavFile!;
        var bytes = await File.ReadAllBytesAsync(wavPath, ct).ConfigureAwait(false);
        int rate = BitConverter.ToInt32(bytes, 24);
        short channels = BitConverter.ToInt16(bytes, 22);
        int dataOffset = 12;
        while (dataOffset + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, dataOffset, 4);
            int size = BitConverter.ToInt32(bytes, dataOffset + 4);
            if (id == "data") { dataOffset += 8; break; }
            dataOffset += 8 + size + (size & 1);
        }
        int pcmBytes = bytes.Length - dataOffset;
        var pcm = new short[pcmBytes / 2];
        Buffer.BlockCopy(bytes, dataOffset, pcm, 0, pcmBytes);

        // Session A: feed the whole file.
        var floatsA = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++) floatsA[i] = pcm[i] / 32768f;
        a.Feed(floatsA, rate);

        // Session B: feed half the file, simulating an active recording.
        var floatsB = new float[pcm.Length / 2];
        for (int i = 0; i < floatsB.Length; i++) floatsB[i] = pcm[i] / 32768f;
        b.Feed(floatsB, rate);

        var swA = Stopwatch.StartNew();
        var resultA = engine.FinalizeBlocking(a);
        swA.Stop();
        log.LogInformation("Session A finalized in {Ms:F0}ms: {Text}", swA.Elapsed.TotalMilliseconds, Truncate(resultA.Text, 60));

        // Session B continues: feed the second half.
        var floatsB2 = new float[pcm.Length - floatsB.Length];
        for (int i = 0; i < floatsB2.Length; i++) floatsB2[i] = pcm[floatsB.Length + i] / 32768f;
        b.Feed(floatsB2, rate);

        var swB = Stopwatch.StartNew();
        var resultB = engine.FinalizeBlocking(b);
        swB.Stop();
        log.LogInformation("Session B finalized in {Ms:F0}ms: {Text}", swB.Elapsed.TotalMilliseconds, Truncate(resultB.Text, 60));

        Console.WriteLine();
        Console.WriteLine($"A ({swA.Elapsed.TotalMilliseconds:F0}ms): {Truncate(resultA.Text, 120)}");
        Console.WriteLine($"B ({swB.Elapsed.TotalMilliseconds:F0}ms): {Truncate(resultB.Text, 120)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
