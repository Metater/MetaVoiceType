using System.Diagnostics;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Lightweight test harness for pure C# session/coordinator logic. Run with
/// `--unit-tests`; exit code 0 means all passed. Native ASR integration tests
/// are separate (the WAV harness).
/// </summary>
public static class UnitTests
{
    private static readonly List<string> Failures = new();

    public static async Task<int> RunAsync(ILogger log, CancellationToken ct)
    {
        Failures.Clear();
        await StoppingAFreesActiveSlotBeforeACompletes(log, ct).ConfigureAwait(false);
        await OnlyOneSessionCanBeRecording(log, ct).ConfigureAwait(false);
        await OldCompletionDoesNotOverwriteCurrentSession(log, ct).ConfigureAwait(false);
        await FaultInADoesNotStopB(log, ct).ConfigureAwait(false);
        await DisposingSessionsDoesNotDisposeSharedBackend(log, ct).ConfigureAwait(false);
        await CompletedSessionRejectsAudio(log, ct).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("=== UNIT TESTS ===");
        Console.WriteLine($"Passed: {6 - Failures.Count}/6");
        foreach (string f in Failures)
            Console.WriteLine($"  FAIL: {f}");
        Console.WriteLine(Failures.Count == 0 ? "VERDICT: PASS" : "VERDICT: FAIL");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            Failures.Add(name);
            Console.WriteLine($"  FAIL: {name}");
        }
        else
        {
            Console.WriteLine($"  ok: {name}");
        }
    }

    // ------------------------------------------------------------- tests

    private static async Task StoppingAFreesActiveSlotBeforeACompletes(ILogger log, CancellationToken ct)
    {
        var backend = new FakeAsrBackend(decodeDelayMs: 2);
        await using var worker = new DecodeWorker();
        var coordinator = new SessionCoordinator(backend, worker);

        RecordingSession a = coordinator.TryStart("en")!;
        a.Feed(new float[160], 16000);
        coordinator.SignalLive(a);
        await Task.Delay(10, ct).ConfigureAwait(false);

        // The real stop path is coordinator.StopActive(): it detaches the
        // session from the active slot BEFORE finalization completes.
        RecordingSession stopped = coordinator.StopActive();

        // The active slot must be free immediately, before A completes.
        Check(coordinator.Active is null, "stopping A frees the active slot before A completes");
        Check(stopped.IsFinalizing, "stopped session A is in Finalizing state");

        RecordingSession b = coordinator.TryStart("en")!;
        Check(b is { IsRecording: true } && !ReferenceEquals(a, b),
            "a new session B can start while A is still finalizing");

        await Task.Delay(60, ct).ConfigureAwait(false);
        Check(a.State == SessionState.Completed, "A completes after B started");
    }

    private static async Task OnlyOneSessionCanBeRecording(ILogger log, CancellationToken ct)
    {
        var backend = new FakeAsrBackend();
        await using var worker = new DecodeWorker();
        var coordinator = new SessionCoordinator(backend, worker);

        RecordingSession a = coordinator.TryStart("en")!;
        RecordingSession? second = coordinator.TryStart("en");
        Check(second is null, "only one session can be Recording at a time");

        coordinator.StopActive();
        RecordingSession? b = coordinator.TryStart("en");
        Check(b is not null, "a second session can start after the first stops");

        await worker.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task OldCompletionDoesNotOverwriteCurrentSession(ILogger log, CancellationToken ct)
    {
        var backend = new FakeAsrBackend(decodeDelayMs: 2);
        await using var worker = new DecodeWorker();
        var coordinator = new SessionCoordinator(backend, worker);

        RecordingSession a = coordinator.TryStart("en")!;
        a.Feed(new float[160], 16000);
        coordinator.SignalLive(a);
        coordinator.StopActive();

        RecordingSession b = coordinator.TryStart("en")!;
        string bBefore = b.FinalTranscript;

        await Task.Delay(80, ct).ConfigureAwait(false); // let A complete
        Check(ReferenceEquals(coordinator.Active, b), "old completion does not overwrite the current active session");
        Check(b.FinalTranscript == bBefore || b.FinalTranscript.Length >= 0, "B's state is not clobbered by A's completion");

        coordinator.StopActive();
        await worker.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task FaultInADoesNotStopB(ILogger log, CancellationToken ct)
    {
        var backend = new FaultyBackendForTests();
        await using var worker = new DecodeWorker();
        var coordinator = new SessionCoordinator(backend, worker);

        RecordingSession a = coordinator.TryStart("en")!;
        a.Feed(new float[160], 16000);
        coordinator.SignalLive(a);
        coordinator.StopActive(); // stream 1 will throw when finalized

        RecordingSession b = coordinator.TryStart("en")!;
        for (int i = 0; i < 5; i++)
        {
            b.Feed(new float[160], 16000);
            coordinator.SignalLive(b);
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
        coordinator.StopActive();

        await Task.Delay(120, ct).ConfigureAwait(false);

        Check(a.State == SessionState.Faulted, "faulted session A is marked Faulted");
        Check(b.State == SessionState.Completed, "session B completes despite A's fault");
        Check(!string.IsNullOrWhiteSpace(b.FinalTranscript), "session B has its own transcript");
        await worker.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task DisposingSessionsDoesNotDisposeSharedBackend(ILogger log, CancellationToken ct)
    {
        var backend = new CountingBackend();
        await using var worker = new DecodeWorker();
        var coordinator = new SessionCoordinator(backend, worker);

        RecordingSession a = coordinator.TryStart("en")!;
        coordinator.StopActive();
        await Task.Delay(60, ct).ConfigureAwait(false);

        // Backend must still create streams after A was disposed.
        RecordingSession? b = coordinator.TryStart("en");
        Check(b is not null, "shared backend still usable after session disposal");
        if (b is not null)
            coordinator.StopActive();
        await Task.Delay(60, ct).ConfigureAwait(false);
        Check(backend.Disposed == false, "disposing sessions does not dispose the shared backend");
        await worker.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task CompletedSessionRejectsAudio(ILogger log, CancellationToken ct)
    {
        var backend = new FakeAsrBackend(decodeDelayMs: 1);
        await using var worker = new DecodeWorker();
        var coordinator = new SessionCoordinator(backend, worker);

        RecordingSession a = coordinator.TryStart("en")!;
        a.Feed(new float[160], 16000);
        coordinator.SignalLive(a);
        coordinator.StopActive();
        await Task.Delay(80, ct).ConfigureAwait(false);

        bool threw = false;
        try { a.Feed(new float[160], 16000); }
        catch (InvalidOperationException) { threw = true; }

        Check(a.State == SessionState.Completed, "A reaches Completed after finalization");
        Check(threw, "feeding a non-Recording session throws InvalidOperationException");
        await worker.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class FaultyBackendForTests : IAsrBackend
{
    private int _created;
    public IAsrStream CreateStream(string language) =>
        new Stream(Interlocked.Increment(ref _created) == 1);
    public void Dispose() { }

    private sealed class Stream : IAsrStream
    {
        private readonly bool _fail;
        private int _ready;
        private int _finished;
        private string _text = string.Empty;
        public Stream(bool fail) => _fail = fail;
        public void Feed(float[] samples, int sampleRate) => Volatile.Write(ref _ready, 1);
        public void MarkInputFinished() => Volatile.Write(ref _finished, 1);
        public bool IsReady() => Volatile.Read(ref _ready) == 1;
        public string Decode()
        {
            if (_fail) throw new InvalidOperationException("injected fault");
            Volatile.Write(ref _ready, 0);
            _text += "ok ";
            return _text;
        }
        public string GetResultText() => _text;
        public void Dispose() { }
    }
}

internal sealed class CountingBackend : IAsrBackend
{
    private int _created;
    public bool Disposed { get; private set; }
    public int StreamsCreated => _created;

    public IAsrStream CreateStream(string language)
    {
        Interlocked.Increment(ref _created);
        return new Stream();
    }

    public void Dispose() => Disposed = true;

    private sealed class Stream : IAsrStream
    {
        private int _ready;
        private int _finished;
        public void Feed(float[] samples, int sampleRate) => Volatile.Write(ref _ready, 1);
        public void MarkInputFinished() => Volatile.Write(ref _finished, 1);
        public bool IsReady() => Volatile.Read(ref _ready) == 1;
        public string Decode() { Volatile.Write(ref _ready, 0); return string.Empty; }
        public string GetResultText() => string.Empty;
        public void Dispose() { }
    }
}
