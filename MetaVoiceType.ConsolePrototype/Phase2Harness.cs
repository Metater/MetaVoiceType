using System.Diagnostics;
using SherpaOnnx;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Phase 2 deterministic concurrency harness. Uses WAV sources so every run
/// is repeatable, and exercises the production architecture: coordinator owns
/// the active slot, the capture pump feeds the bound session, and all decode
/// work flows through the single DecodeWorker.
/// </summary>
public sealed class Phase2Harness
{
    private readonly Options _opts;
    private readonly ILogger _log;
    private readonly IAsrBackend _backend;
    private readonly string _modelRoot;
    private readonly DecodeWorker _worker;
    private readonly SessionCoordinator _coordinator;

    private double _maxTransitionMs;
    private double _maxFinalizeLatencyMs;
    private double _maxDecodeMs;
    private long _maxQueueDepth;
    private readonly List<string> _violations = new();

    public Phase2Harness(Options opts, ILogger log, IAsrBackend backend)
    {
        _opts = opts;
        _log = log;
        _backend = backend;
        _worker = new DecodeWorker();
        _coordinator = new SessionCoordinator(backend, _worker);
        _modelRoot = Path.GetDirectoryName(opts.Tokens)!;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            await RunOverlapAsync(ct).ConfigureAwait(false);
            await RunStressAsync(ct).ConfigureAwait(false);
            await RunFaultIsolationAsync(ct).ConfigureAwait(false);
            await RunLongRunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await _worker.DisposeAsync().ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine("=== PHASE 2 HARNESS RESULTS ===");
        Console.WriteLine($"Max transition Stop(A) -> B first feed : {_maxTransitionMs:F2} ms");
        Console.WriteLine($"Max finalization latency (queued->done): {_maxFinalizeLatencyMs:F2} ms");
        Console.WriteLine($"Max decode step duration               : {_maxDecodeMs:F2} ms");
        Console.WriteLine($"Max decode queue depth                 : {_maxQueueDepth}");
        Console.WriteLine($"Violations                             : {_violations.Count}");
        foreach (string v in _violations)
            Console.WriteLine($"  - {v}");
        Console.WriteLine(_violations.Count == 0 ? "VERDICT: PASS" : "VERDICT: FAIL");
        return _violations.Count == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- helpers

    private WavFileSource Source(string file, int repeat = 1) =>
        new(Path.Combine(_modelRoot, "test_wavs", file), _opts.WavChunkMs, repeat, _log);

    private async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int timeoutMs = 60000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
            await Task.Delay(10, ct).ConfigureAwait(false);
        if (!condition())
            throw new TimeoutException("Condition not met within timeout.");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private void SnapshotMetrics()
    {
        _maxDecodeMs = Math.Max(_maxDecodeMs, _worker.LastDecodeMs);
        _maxQueueDepth = Math.Max(_maxQueueDepth, _worker.QueueDepth);
    }

    // ------------------------------------------------------------- TEST 1

    private async Task RunOverlapAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("=== TEST 1: A finalizes while B records (es + de) ===");
        _log.LogInformation("PHASE2 OVERLAP START");

        RecordingSession? a = _coordinator.TryStart("es");
        if (a is null) { Violate("overlap: could not start A"); return; }

        using var srcA = Source("es.wav");
        var pumpA = new CapturePump(srcA, _coordinator, boundSession: a);

        var transitionSw = new Stopwatch();

        // Start A; stop it mid-stream; then immediately start B.
        var pumpATask = Task.Run(async () =>
        {
            await pumpA.RunAsync(ct).ConfigureAwait(false);
        }, ct);

        await WaitUntilAsync(() => a.AudioSecondsFed > 1.5, ct).ConfigureAwait(false);

        transitionSw.Start();
        // Production stop path: detaches the slot and queues finalization.
        _coordinator.StopActive();
        _log.LogInformation("PHASE2 A stopped; finalize queued. Active slot is now free.");

        RecordingSession? b = _coordinator.TryStart("de");
        if (b is null)
        {
            Violate("overlap: active slot not freed after A.Stop()");
            transitionSw.Stop();
        }
        else
        {
            using var srcB = Source("de.wav");
            var pumpB = new CapturePump(srcB, _coordinator, boundSession: b);
            var pumpBTask = pumpB.RunAsync(ct);

            // Measure: Stop(A) -> B accepts first frame.
            await WaitUntilAsync(() => b.SamplesFed > 0, ct).ConfigureAwait(false);
            transitionSw.Stop();
            _maxTransitionMs = Math.Max(_maxTransitionMs, transitionSw.Elapsed.TotalMilliseconds);
            _log.LogInformation("PHASE2 TRANSITION Stop(A)->B-first-feed = {Ms:F2} ms",
                transitionSw.Elapsed.TotalMilliseconds);

            // B keeps recording while A finalizes in the background.
            await WaitUntilAsync(() => b.AudioSecondsFed > 1.5, ct).ConfigureAwait(false);
            _coordinator.StopActive(); // B is the active session
            await pumpBTask.ConfigureAwait(false);
        }

        await pumpATask.ConfigureAwait(false);

        // Both sessions must reach a terminal state.
        await WaitUntilAsync(() =>
            a.State is SessionState.Completed or SessionState.Faulted &&
            (b is null || b.State is SessionState.Completed or SessionState.Faulted),
            ct).ConfigureAwait(false);

        foreach (var s in new[] { a, b })
        {
            if (s is null) continue;
            _log.LogInformation("PHASE2 overlap session {Id} lang={Lang} state={State} text={Text}",
                s.Id, s.Language, s.State, Truncate(s.FinalTranscript, 70));
            _maxFinalizeLatencyMs = Math.Max(_maxFinalizeLatencyMs, s.FinalizationLatencyMs ?? 0);
            SnapshotMetrics();
        }

        // Isolation assertions. The first 0.4s of each WAV carries its
        // language's opening words; check for non-empty text and confirm no
        // cross-language leakage rather than exact word positions (sessions
        // stop early by design).
        if (a.State == SessionState.Completed)
        {
            if (string.IsNullOrWhiteSpace(a.FinalTranscript))
                Violate("overlap: session A (es) has empty transcript");
            if (a.FinalTranscript.Contains("dich", StringComparison.OrdinalIgnoreCase) &&
                !a.FinalTranscript.Contains("dicho", StringComparison.OrdinalIgnoreCase))
                Violate("overlap: session A (es) leaked German text");
        }
        else
        {
            Violate($"overlap: session A (es) state is {a.State}, expected Completed");
        }

        if (b is { State: SessionState.Completed })
        {
            if (string.IsNullOrWhiteSpace(b.FinalTranscript))
                Violate("overlap: session B (de) has empty transcript");
            if (b.FinalTranscript.Contains("preguntes", StringComparison.OrdinalIgnoreCase))
                Violate("overlap: session B (de) leaked Spanish text");
        }
        else if (b is not null)
        {
            Violate($"overlap: session B (de) state is {b.State}, expected Completed");
        }
    }

    // ------------------------------------------------------------- TEST 2

    private async Task RunStressAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("=== TEST 2: A->B->C->D rapid chain (es/de/fr/uk) ===");
        _log.LogInformation("PHASE2 STRESS START");

        string[] files = ["es.wav", "de.wav", "fr.wav", "uk.wav"];
        string[] langs = ["es", "de", "fr", "uk"];
        int stressSessionCount = _coordinator.All.Count + files.Length;

        foreach (string f in files)
        {
            string lang = langs[Array.IndexOf(files, f)];
            RecordingSession? s = _coordinator.TryStart(lang);
            if (s is null)
            {
                Violate($"stress: could not start session for {f}");
                continue;
            }

            using var src = Source(f);
            var pump = new CapturePump(src, _coordinator, boundSession: s);
            var pumpTask = pump.RunAsync(ct);

            await WaitUntilAsync(() => s.AudioSecondsFed > 1.5, ct).ConfigureAwait(false);
            _coordinator.StopActive(); // production stop path
            await pumpTask.ConfigureAwait(false);
            // The next loop iteration starts the next session immediately.
        }

        await WaitUntilAsync(() =>
            _coordinator.All.Count == stressSessionCount &&
            _coordinator.All.All(s => s.State is SessionState.Completed or SessionState.Faulted),
            ct).ConfigureAwait(false);

        var all = _coordinator.All;
        for (int i = 0; i < all.Count; i++)
        {
            RecordingSession s = all[i];
            _log.LogInformation("PHASE2 stress session {Id} lang={Lang} state={State} text={Text}",
                s.Id, s.Language, s.State, Truncate(s.FinalTranscript, 50));
            if (s.State == SessionState.Faulted)
                Violate($"stress: session {s.Id} faulted: {s.Fault?.Message}");
            if (string.IsNullOrWhiteSpace(s.FinalTranscript))
                Violate($"stress: session {s.Id} has empty transcript");
            _maxFinalizeLatencyMs = Math.Max(_maxFinalizeLatencyMs, s.FinalizationLatencyMs ?? 0);
            SnapshotMetrics();
        }

        // Cross-session contamination: no session's text may contain another
        // session's full distinctive opening words.
        for (int i = 0; i < all.Count; i++)
        {
            for (int j = 0; j < all.Count; j++)
            {
                if (i == j) continue;
                string own = all[i].FinalTranscript;
                string other = all[j].FinalTranscript;
                if (own.Length > 30 && other.Length > 30)
                {
                    string signature = other[..Math.Min(30, other.Length)];
                    if (own.Contains(signature, StringComparison.OrdinalIgnoreCase))
                        Violate($"stress: session {all[i].Id} contains session {all[j].Id}'s text");
                }
            }
        }
    }

    // ------------------------------------------------------------- TEST 3

    private async Task RunFaultIsolationAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("=== TEST 3: Fault isolation (A faults, B keeps recording) ===");
        _log.LogInformation("PHASE2 FAULT START");

        // Use a separate fake backend so we can inject faults without touching
        // the shared production recognizer.
        var fakeBackend = new FaultyAsrBackend();
        var fakeWorker = new DecodeWorker();
        var fakeCoordinator = new SessionCoordinator(fakeBackend, fakeWorker);

        RecordingSession? a = fakeCoordinator.TryStart("en");
        if (a is null) { Violate("fault: could not start A"); return; }

        a.Feed(new float[160], 16000);
        fakeCoordinator.SignalLive(a);
        await Task.Delay(50, ct).ConfigureAwait(false);

        // Use the coordinator's production stop path; the injected fault in
        // A's stream will surface when the worker finalizes it.
        fakeCoordinator.StopActive();

        // Start B immediately; it must survive A's fault.
        RecordingSession? b = fakeCoordinator.TryStart("en");
        if (b is null) { Violate("fault: active slot not freed after A.Stop()"); return; }

        for (int i = 0; i < 10; i++)
        {
            b.Feed(new float[160], 16000);
            fakeCoordinator.SignalLive(b);
            await Task.Delay(15, ct).ConfigureAwait(false);
        }
        fakeCoordinator.StopActive();

        await WaitUntilAsync(() =>
            a.State is SessionState.Completed or SessionState.Faulted &&
            b.State is SessionState.Completed or SessionState.Faulted,
            ct).ConfigureAwait(false);

        _log.LogInformation("PHASE2 FAULT a.state={AState} b.state={BState} b.text={Text}",
            a.State, b.State, Truncate(b.FinalTranscript, 40));

        if (a.State != SessionState.Faulted)
            Violate($"fault: A should be Faulted, is {a.State}");
        if (b.State != SessionState.Completed)
            Violate($"fault: B should be Completed, is {b.State}");
        if (string.IsNullOrWhiteSpace(b.FinalTranscript))
            Violate("fault: B has empty final transcript");

        await fakeWorker.DisposeAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------- TEST 4

    private async Task RunLongRunAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("=== TEST 4: Long run with transitions (~10 min simulated) ===");
        _log.LogInformation("PHASE2 LONG START");

        // Session A: short, stops almost immediately.
        RecordingSession? a = _coordinator.TryStart("es");
        if (a is null) { Violate("long: could not start A"); return; }
        {
            using var srcA = Source("es.wav");
            var pumpA = new CapturePump(srcA, _coordinator, boundSession: a);
            var pumpATask = pumpA.RunAsync(ct);
            await WaitUntilAsync(() => a.AudioSecondsFed > 0.3, ct).ConfigureAwait(false);
            _coordinator.StopActive();
            await pumpATask.ConfigureAwait(false);
        }

        // Session B: long continuous recording (~10 min of real-time audio).
        // 60 passes of the 5.3s Spanish clip ≈ 320s; use the larger repeat to
        // reach ~10 minutes: 5.3s * 110 ≈ 583s ≈ 9.7 min.
        int bRepeats = _opts.WavRepeat > 1 ? _opts.WavRepeat : 110;
        RecordingSession? b = _coordinator.TryStart("es");
        if (b is null) { Violate("long: could not start B after A stopped"); return; }

        long maxQueue = 0;
        {
            using var srcB = Source("es.wav", repeat: bRepeats);
            var pumpB = new CapturePump(srcB, _coordinator, boundSession: b);
            var pumpBTask = pumpB.RunAsync(ct);

            // Sample metrics while B records. Transitions during a long
            // recording are impossible by design (one active at a time);
            // the stress test covers rapid A→B→C→D chains instead.
            var monitor = Task.Run(async () =>
            {
                while (!pumpBTask.IsCompleted && !ct.IsCancellationRequested)
                {
                    maxQueue = Math.Max(maxQueue, _worker.QueueDepth);
                    SnapshotMetrics();
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
            }, ct);

            await monitor.ConfigureAwait(false);
            await pumpBTask.ConfigureAwait(false);
            _coordinator.StopActive();
        }

        await WaitUntilAsync(() =>
            _coordinator.All.All(s => s.State is SessionState.Completed or SessionState.Faulted),
            ct, timeoutMs: 120000).ConfigureAwait(false);

        _log.LogInformation("PHASE2 LONG b.audio={Audio:F1}s b.final={Text}",
            b.AudioSecondsFed, Truncate(b.FinalTranscript, 60));
        _maxQueueDepth = Math.Max(_maxQueueDepth, maxQueue);

        if (b.AudioSecondsFed < 500)
            Violate($"long: B only received {b.AudioSecondsFed:F1}s audio (expected ~10 min)");
        if (b.State != SessionState.Completed)
            Violate($"long: B state is {b.State}, expected Completed");
        if (string.IsNullOrWhiteSpace(b.FinalTranscript))
            Violate("long: B has empty transcript");
        if (b.FinalizationLatencyMs is { } lat && lat > 500)
            Violate($"long: B finalization took {lat:F1}ms (>500ms)");
        _maxFinalizeLatencyMs = Math.Max(_maxFinalizeLatencyMs, b.FinalizationLatencyMs ?? 0);
    }

    // -------------------------------------------------------------- metrics

    private void Violate(string message)
    {
        lock (_violations)
        {
            _violations.Add(message);
            _log.LogError("PHASE2 VIOLATION: {Message}", message);
        }
    }
}

/// <summary>
/// Fake backend that throws when the FIRST stream it creates is decoded, and
/// behaves normally for later streams. Used only by the fault-isolation test:
/// session A (first stream) faults, session B (second stream) must survive.
/// </summary>
internal sealed class FaultyAsrBackend : IAsrBackend
{
    private int _streamsCreated;

    public IAsrStream CreateStream(string language)
    {
        int n = Interlocked.Increment(ref _streamsCreated);
        return new FaultyStream(fail: n == 1);
    }

    public void Dispose() { }

    private sealed class FaultyStream : IAsrStream
    {
        private readonly bool _fail;
        private int _ready;
        private int _finished;
        private int _disposed;
        private string _text = string.Empty;

        public FaultyStream(bool fail) => _fail = fail;

        public void Feed(float[] samples, int sampleRate) => Volatile.Write(ref _ready, 1);

        /// <summary>Simulates a pending tail so the finalize drain runs at least once.</summary>
        public void MarkInputFinished()
        {
            Volatile.Write(ref _finished, 1);
            Volatile.Write(ref _ready, 1);
        }

        /// <summary>Ready only while decodeable audio is pending (sherpa semantics).</summary>
        public bool IsReady() =>
            Volatile.Read(ref _ready) == 1 && Volatile.Read(ref _disposed) == 0;

        public string Decode()
        {
            if (_fail && Volatile.Read(ref _finished) == 1)
                throw new InvalidOperationException("injected stream fault (finalize only)");
            Volatile.Write(ref _ready, 0);
            Volatile.Write(ref _finished, 0);
            _text += "ok ";
            return _text;
        }

        public string GetResultText() => _text;
        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }
}
