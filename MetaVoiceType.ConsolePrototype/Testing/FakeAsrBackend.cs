using System.Text;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Deterministic in-memory ASR backend for unit tests. Models sherpa-onnx
/// semantics: Feed adds pending work, IsReady is true while work is pending or
/// after InputFinished until drained, Decode consumes pending work and appends
/// stream-unique text. No native dependencies.
/// </summary>
public sealed class FakeAsrBackend : IAsrBackend
{
    private readonly int _tagLength;
    private readonly int _decodeDelayMs;
    private readonly int _maxPendingPerFeed;

    public FakeAsrBackend(int tagLength = 4, int decodeDelayMs = 1, int maxPendingPerFeed = 3)
    {
        _tagLength = tagLength;
        _decodeDelayMs = decodeDelayMs;
        _maxPendingPerFeed = maxPendingPerFeed;
    }

    public IAsrStream CreateStream(string language)
    {
        string tag = Guid.NewGuid().ToString("N")[.._tagLength];
        return new FakeStream($"{tag}:{language}", _decodeDelayMs, _maxPendingPerFeed);
    }

    public void Dispose() { }

    private sealed class FakeStream : IAsrStream
    {
        private readonly string _tag;
        private readonly int _decodeDelayMs;
        private readonly int _maxPendingPerFeed;
        private readonly StringBuilder _text = new();
        private readonly Random _rng;
        private int _pending;
        private int _finished;
        private int _disposed;

        public FakeStream(string tag, int decodeDelayMs, int maxPendingPerFeed)
        {
            _tag = tag;
            _decodeDelayMs = decodeDelayMs;
            _maxPendingPerFeed = maxPendingPerFeed;
            _rng = new Random(tag.GetHashCode());
        }

        public void Feed(float[] samples, int sampleRate)
        {
            if (Volatile.Read(ref _disposed) == 1)
                throw new ObjectDisposedException(nameof(FakeStream));
            Interlocked.Add(ref _pending, _rng.Next(1, _maxPendingPerFeed + 1));
        }

        public void MarkInputFinished() => Volatile.Write(ref _finished, 1);

        /// <summary>
        /// Mirrors sherpa-onnx: ready while decodeable audio is pending.
        /// After InputFinished the stream is ready only until the pending
        /// tail is drained, then reports not-ready (final).
        /// </summary>
        public bool IsReady() => Volatile.Read(ref _pending) > 0;

        public string Decode()
        {
            if (Volatile.Read(ref _disposed) == 1)
                throw new ObjectDisposedException(nameof(FakeStream));
            if (Volatile.Read(ref _pending) > 0)
                Interlocked.Decrement(ref _pending);
            if (_decodeDelayMs > 0)
                Thread.Sleep(_decodeDelayMs);
            _text.Append(_tag).Append(' ');
            return _text.ToString();
        }

        public string GetResultText() => _text.ToString();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Volatile.Write(ref _pending, 0);
        }
    }
}
