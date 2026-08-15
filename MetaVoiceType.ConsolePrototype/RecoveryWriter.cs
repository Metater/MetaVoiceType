using System.Text.Json;
using System.Threading.Channels;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Temporary PCM recovery file format: raw 16 kHz mono PCM16 plus a
/// sidecar JSON metadata file. Raw PCM survives unclean termination better
/// than a half-written WAV header, and the sidecar is rewritten atomically
/// so a crash never leaves corrupt metadata.
/// </summary>
public sealed record RecoveryMetadata(
    string SessionId,
    string Language,
    DateTimeOffset StartedAtUtc,
    string State,
    long SamplesWritten,
    int SampleRate,
    DateTimeOffset LastUpdatedUtc);

/// <summary>
/// Writes per-session temporary recovery audio on a dedicated worker so
/// disk I/O never runs on the capture callback. Consumed frames are plain
/// float[] (16 kHz) and are converted to PCM16 in the worker.
/// </summary>
public sealed class RecoveryWriter : IAsyncDisposable
{
    private readonly string _directory;
    private readonly ILogger _log;
    private readonly Channel<RecoveryWork> _queue;
    private readonly Task _loop;
    private readonly CancellationTokenSource _lifetime = new();

    private int _queueDepth;
    private long _maxObservedDepth;
    private long _bytesWritten;

    private sealed record RecoveryWork(string SessionId, string Language, float[] Frame, bool Finalize);
    private sealed class StreamState
    {
        public StreamState(FileStream file, string path, RecoveryMetadata metadata)
        {
            File = file;
            Path = path;
            Metadata = metadata;
        }

        public FileStream File { get; }
        public string Path { get; }
        public RecoveryMetadata Metadata { get; set; }
    }

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    public long MaxObservedDepth => Interlocked.Read(ref _maxObservedDepth);
    public int QueueDepth => Volatile.Read(ref _queueDepth);
    public string Directory => _directory;

    public RecoveryWriter(string directory, ILogger log)
    {
        _directory = directory;
        _log = log;
        System.IO.Directory.CreateDirectory(directory);
        _queue = Channel.CreateUnbounded<RecoveryWork>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        _loop = Task.Run(LoopAsync);
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MetaVoiceType", "recovery");

    public string PathFor(string sessionId) => Path.Combine(_directory, $"{sessionId}.pcm");
    public string MetadataPathFor(string sessionId) => Path.Combine(_directory, $"{sessionId}.json");

    /// <summary>Append one frame for a session. Never blocks; enqueue only.</summary>
    public void Enqueue(string sessionId, string language, float[] frame)
    {
        // Copy the frame so the capture pipeline can reuse its buffer.
        var copy = new float[frame.Length];
        Array.Copy(frame, copy, frame.Length);
        if (_queue.Writer.TryWrite(new RecoveryWork(sessionId, language, copy, false)))
        {
            Interlocked.Increment(ref _queueDepth);
            UpdateMaxDepth(Volatile.Read(ref _queueDepth));
        }
        else
        {
            _log.LogError("Recovery writer queue rejected a frame for session {Id} — audio may be unrecoverable.", sessionId);
        }
    }

    /// <summary>Request a session's stream be closed and its metadata finalized.</summary>
    public void FinalizeSession(string sessionId)
    {
        if (_queue.Writer.TryWrite(new RecoveryWork(sessionId, string.Empty, Array.Empty<float>(), true)))
        {
            Interlocked.Increment(ref _queueDepth);
            UpdateMaxDepth(Volatile.Read(ref _queueDepth));
        }
    }

    private void UpdateMaxDepth(int depth)
    {
        long max = Interlocked.Read(ref _maxObservedDepth);
        while (depth > max)
        {
            long current = Interlocked.CompareExchange(ref _maxObservedDepth, depth, max);
            if (current == max)
                break;
            max = current;
        }
    }

    private async Task LoopAsync()
    {
        var streams = new Dictionary<string, StreamState>();
        try
        {
            await foreach (RecoveryWork work in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queueDepth);
                try
                {
                    if (!streams.TryGetValue(work.SessionId, out var state))
                    {
                        state = OpenStream(work.SessionId, work.Language, work.Finalize ? "finalizing" : "recording");
                        streams[work.SessionId] = state;
                    }

                    if (work.Frame.Length > 0)
                    {
                        WritePcm16(state, work.Frame);
                    }

                    if (work.Finalize)
                    {
                        await state.File.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                        await state.File.DisposeAsync().ConfigureAwait(false);
                        await WriteMetadataAsync(state, "finalizing").ConfigureAwait(false);
                        streams.Remove(work.SessionId);
                        _log.LogInformation("Recovery stream closed for session {Id}: {Samples} samples.",
                            work.SessionId, state.Metadata.SamplesWritten);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Recovery writer failed processing work for session {Id}.", work.SessionId);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var (id, state) in streams)
            {
                try
                {
                    await state.File.FlushAsync().ConfigureAwait(false);
                    await state.File.DisposeAsync().ConfigureAwait(false);
                    await WriteMetadataAsync(state, "recording").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Recovery writer failed closing stream for session {Id} at shutdown.", id);
                }
            }
        }
    }

    private StreamState OpenStream(string sessionId, string language, string state)
    {
        string path = PathFor(sessionId);
        var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        var metadata = new RecoveryMetadata(sessionId, language,
            DateTimeOffset.UtcNow, state, 0, 16000, DateTimeOffset.UtcNow);
        _log.LogInformation("Recovery stream opened: {Path}", path);
        return new StreamState(file, path, metadata);
    }

    private void WritePcm16(StreamState state, float[] frame)
    {
        var pcm = new byte[frame.Length * 2];
        for (int i = 0; i < frame.Length; i++)
        {
            short s = (short)Math.Clamp((int)(frame[i] * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        state.File.Write(pcm, 0, pcm.Length);
        Interlocked.Add(ref _bytesWritten, pcm.Length);
        state.Metadata = state.Metadata with
        {
            SamplesWritten = state.Metadata.SamplesWritten + frame.Length,
            LastUpdatedUtc = DateTimeOffset.UtcNow
        };
    }

    private async Task WriteMetadataAsync(StreamState state, string finalState)
    {
        string metaPath = MetadataPathFor(state.Metadata.SessionId);
        string tmpPath = metaPath + ".tmp";
        var final = state.Metadata with { State = finalState, LastUpdatedUtc = DateTimeOffset.UtcNow };
        await using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(fs, final, JsonOptions).ConfigureAwait(false);
            await fs.FlushAsync().ConfigureAwait(false);
        }
        File.Move(tmpPath, metaPath, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        _lifetime.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { /* cancelled */ }
        _lifetime.Dispose();
    }

    private int _disposed;
}
