using System.Threading.Channels;
using MetaVoiceType.Core.Interfaces;

namespace MetaVoiceType.Audio;

public sealed class AudioFrameBuffer
{
    private readonly Channel<byte[]> _channel;
    private long _captured;
    private long _dispatched;
    private long _dropped;
    private long _samplesQueued;
    private int _depth;
    private int _highWater;

    public AudioFrameBuffer(int capacity = 3_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public int Depth => Volatile.Read(ref _depth);
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public bool TryEnqueue(byte[] frame)
    {
        Interlocked.Increment(ref _captured);
        if (!_channel.Writer.TryWrite(frame)) { Interlocked.Increment(ref _dropped); return false; }
        Interlocked.Add(ref _samplesQueued, frame.LongLength / sizeof(short));
        int depth = Interlocked.Increment(ref _depth);
        int maximum = Volatile.Read(ref _highWater);
        while (depth > maximum)
        {
            int found = Interlocked.CompareExchange(ref _highWater, depth, maximum);
            if (found == maximum) break;
            maximum = found;
        }
        return true;
    }

    public async IAsyncEnumerable<byte[]> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (byte[] frame in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _depth);
            Interlocked.Increment(ref _dispatched);
            yield return frame;
        }
    }

    public AudioMetrics Snapshot(double callbackMilliseconds) => new(Interlocked.Read(ref _captured), Depth,
        Volatile.Read(ref _highWater), Interlocked.Read(ref _dropped), callbackMilliseconds, Interlocked.Read(ref _dispatched),
        Interlocked.Read(ref _samplesQueued));
    public void Complete() => _channel.Writer.TryComplete();
}
