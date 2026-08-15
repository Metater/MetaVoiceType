using System.Diagnostics;
using System.Text.Json;
using SherpaOnnx;

namespace MetaVoiceType.ConsolePrototype;

/// <summary>
/// Phase 3 durable recovery + history test harness. Run with --phase3.
/// Uses the SAME Sherpa backend, DecodeWorker, and SessionCoordinator as
/// live recording; recovery is just another finalize fed from PCM files.
/// </summary>
public sealed class Phase3Harness
{
    private readonly Options _opts;
    private readonly ILogger _log;
    private readonly IAsrBackend _backend;
    private readonly string _modelRoot;
    private readonly List<string> _violations = new();
    private readonly string _scratch;

    public Phase3Harness(Options opts, ILogger log, IAsrBackend backend)
    {
        _opts = opts;
        _log = log;
        _backend = backend;
        _modelRoot = Path.GetDirectoryName(opts.Tokens)!;
        _scratch = Path.Combine(Path.GetTempPath(), "mvt-phase3-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_scratch);
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            await RecoveryRoundTripAsync(ct).ConfigureAwait(false);
            await HistoryRetentionAsync(ct).ConfigureAwait(false);
            await RecoveryPlusLiveRecordingAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(_scratch, recursive: true); } catch { /* scratch cleanup */ }
        }

        Console.WriteLine();
        Console.WriteLine("=== PHASE 3 HARNESS RESULTS ===");
        Console.WriteLine($"Violations : {_violations.Count}");
        foreach (string v in _violations)
            Console.WriteLine($"  - {v}");
        Console.WriteLine(_violations.Count == 0 ? "VERDICT: PASS" : "VERDICT: FAIL");
        return _violations.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ helpers

    private WavFileSource Source(string file, int repeat = 1) =>
        new(Path.Combine(_modelRoot, "test_wavs", file), _opts.WavChunkMs, repeat, _log);

    private async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct, int timeoutMs = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested)
            await Task.Delay(10, ct).ConfigureAwait(false);
        if (!condition())
            throw new TimeoutException("Condition not met within timeout.");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private void Violate(string message)
    {
        lock (_violations)
        {
            _violations.Add(message);
            _log.LogError("PHASE3 VIOLATION: {Message}", message);
        }
    }

    // ------------------------------------------------------------ TEST A/D

    private async Task RecoveryRoundTripAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("=== TEST A+D: recovery round trip + successful completion ===");
        _log.LogInformation("PHASE3 ROUNDTRIP START");

        // Simulate an interrupted session: write PCM to the recovery dir
        // exactly like the RecoveryWriter would, without the final commit.
        string recoveryDir = Path.Combine(_scratch, "recovery");
        Directory.CreateDirectory(recoveryDir);
        const string sessionId = "S0001";
        string pcmPath = Path.Combine(recoveryDir, $"{sessionId}.pcm");

        // Feed ~4s of Spanish WAV through a pump into a session whose
        // recovery writer captured the frames, then simulate a crash by
        // disposing the writer without finalizing (metadata left as
        // "recording").
        {
            await using var worker = new DecodeWorker();
            var coordinator = new SessionCoordinator(_backend, worker);
            await using var writer = new RecoveryWriter(recoveryDir, _log);

            RecordingSession? session = coordinator.TryStart("es");
            if (session is null) { Violate("roundtrip: could not start session"); return; }

            using var src = Source("es.wav");
            var pump = new CapturePump(src, coordinator, boundSession: session,
                onFrame: (s, frame) => writer.Enqueue(s.Id, s.Language, frame));
            var pumpTask = pump.RunAsync(ct);

            await WaitUntilAsync(() => session.AudioSecondsFed > 3.0, ct).ConfigureAwait(false);
            coordinator.StopActive();
            await pumpTask.ConfigureAwait(false);

            // Wait for the recovery writer to finish writing the tail.
            await Task.Delay(800, ct).ConfigureAwait(false);
            // Simulate crash: dispose writer WITHOUT FinalizeSession — the PCM
            // remains on disk with a "recording" state in metadata.
            await writer.DisposeAsync().ConfigureAwait(false);
            await worker.DisposeAsync().ConfigureAwait(false);
        }

        bool pcmExists = File.Exists(pcmPath) && new FileInfo(pcmPath).Length > 0;
        Check(pcmExists, "roundtrip: recovery PCM survives simulated crash");

        // Startup recovery: discover and replay with a FRESH worker/coordinator
        // (simulating a restarted application).
        var recovery = new RecoveryService(recoveryDir, _log);
        var discovered = recovery.Discover().ToList();
        Check(discovered.Contains(sessionId), "roundtrip: recovery service discovers the session");

        string historyPath = Path.Combine(_scratch, "history.json");
        var history = new HistoryStore(historyPath, log: _log);
        {
            await using var worker = new DecodeWorker();
            var coordinator = new SessionCoordinator(_backend, worker);
            int recovered = await RecoverAllAsync(recovery, history, coordinator, worker, ct).ConfigureAwait(false);
            Check(recovered == 1, "roundtrip: exactly one session recovered");
        }

        var entries = await history.LoadAsync(ct).ConfigureAwait(false);
        Check(entries.Count == 1 && entries[0].SessionId == sessionId,
            "roundtrip: recovered transcript committed to history");
        Check(entries.Count > 0 && !string.IsNullOrWhiteSpace(entries[0].Transcript),
            "roundtrip: recovered transcript is non-empty");
        Check(!File.Exists(pcmPath), "roundtrip: recovery PCM deleted after durable commit");
    }

    // ------------------------------------------------------------ TEST E

    private async Task HistoryRetentionAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("=== TEST E: 100-entry retention (oldest eligible pruned) ===");
        _log.LogInformation("PHASE3 RETENTION START");

        string historyPath = Path.Combine(_scratch, "history-retention.json");
        var history = new HistoryStore(historyPath, limit: 10, log: _log);

        var entries = new List<HistoryEntry>();
        var baseTime = DateTimeOffset.UtcNow.AddDays(-200);
        for (int i = 0; i < 12; i++)
        {
            entries.Add(new HistoryEntry(
                $"R{i:D4}", baseTime.AddDays(i), baseTime.AddDays(i).AddMinutes(2),
                120, "Completed", "en", $"transcript {i}", false, false, false));
        }

        await history.ReplaceAsync(entries, ct).ConfigureAwait(false);
        var loaded = await history.LoadAsync(ct).ConfigureAwait(false);
        Check(loaded.Count == 10, $"retention: 12 completed entries pruned to 10 (got {loaded.Count})");
        Check(loaded.All(e => e.SessionId != "R0000") &&
              loaded.All(e => e.SessionId != "R0001"),
            "retention: the two OLDEST entries were removed");
        Check(loaded.Any(e => e.SessionId == "R0011"),
            "retention: the newest entry is retained");

        // A non-eligible (recoverable) session must survive pruning.
        var withRecoverable = new List<HistoryEntry>(loaded);
        withRecoverable.Insert(0, new HistoryEntry(
            "R-RECOVER", baseTime.AddDays(-300), null, 30, "Recoverable", "en",
            "incomplete", false, false, false));
        await history.ReplaceAsync(withRecoverable, ct).ConfigureAwait(false);
        var loaded2 = await history.LoadAsync(ct).ConfigureAwait(false);
        Check(loaded2.Any(e => e.SessionId == "R-RECOVER"),
            "retention: recoverable session is NOT pruned by the completed-history limit");
    }

    // ------------------------------------------------------------ TEST F

    private async Task RecoveryPlusLiveRecordingAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("=== TEST F: recovery in background + live recording ===");
        _log.LogInformation("PHASE3 RECOVERY+LIVE START");

        string recoveryDir = Path.Combine(_scratch, "recovery2");
        Directory.CreateDirectory(recoveryDir);

        // Write a fake interrupted session directly.
        string sessionId = "S-OLD-0001";
        string pcmPath = Path.Combine(recoveryDir, $"{sessionId}.pcm");
        using (var src = Source("de.wav", repeat: 1))
        {
            var frames = new List<float[]>();
            await foreach (var f in src.ReadFramesAsync(ct).ConfigureAwait(false))
                frames.Add(f);
            using var fs = new FileStream(pcmPath, FileMode.Create, FileAccess.Write);
            foreach (var f in frames)
            {
                for (int i = 0; i < f.Length; i++)
                {
                    short s = (short)Math.Clamp((int)(f[i] * 32767f), short.MinValue, short.MaxValue);
                    fs.WriteByte((byte)(s & 0xFF));
                    fs.WriteByte((byte)((s >> 8) & 0xFF));
                }
            }
        }
        await File.WriteAllTextAsync(Path.Combine(recoveryDir, $"{sessionId}.json"),
            JsonSerializer.Serialize(new RecoveryMetadata(sessionId, "de", DateTimeOffset.UtcNow.AddMinutes(-5),
                "recording", 0, 16000, DateTimeOffset.UtcNow.AddMinutes(-5))),
            ct).ConfigureAwait(false);

        // Start the recovery replay in the background.
        await using var worker = new DecodeWorker();
        var coordinator = new SessionCoordinator(_backend, worker);
        var recovery = new RecoveryService(recoveryDir, _log);
        string historyPath = Path.Combine(_scratch, "history-reclive.json");
        var history = new HistoryStore(historyPath, log: _log);

        var recoveryTask = Task.Run(() => RecoverAllAsync(recovery, history, coordinator, worker, ct), ct);

        // IMMEDIATELY start a live recording — it must not be blocked.
        var transitionSw = Stopwatch.StartNew();
        RecordingSession? live = coordinator.TryStart("es");
        if (live is null)
        {
            Violate("recovery+live: live session was blocked by recovery");
            await recoveryTask.ConfigureAwait(false);
            return;
        }

        using var liveSrc = Source("es.wav");
        var pump = new CapturePump(liveSrc, coordinator, boundSession: live);
        var pumpTask = pump.RunAsync(ct);
        await WaitUntilAsync(() => live.AudioSecondsFed > 1.5, ct).ConfigureAwait(false);
        transitionSw.Stop();
        _log.LogInformation("PHASE3 RECOVERY+LIVE transition to live session: {Ms:F2} ms",
            transitionSw.Elapsed.TotalMilliseconds);
        Check(transitionSw.Elapsed.TotalMilliseconds < 2000,
            "recovery+live: live recording starts while recovery processes (no blocking)");

        coordinator.StopActive();
        await pumpTask.ConfigureAwait(false);
        await recoveryTask.ConfigureAwait(false);

        var entries = await history.LoadAsync(ct).ConfigureAwait(false);
        bool hasOld = entries.Any(e => e.SessionId == sessionId);
        Check(hasOld, "recovery+live: recovered old session committed to history");
        Check(!File.Exists(pcmPath), "recovery+live: old session PCM deleted after commit");
    }

    // ------------------------------------------------------------ runner

    private async Task<int> RecoverAllAsync(RecoveryService recovery, HistoryStore history,
        SessionCoordinator coordinator, DecodeWorker worker, CancellationToken ct)
    {
        int recovered = 0;
        foreach (string sessionId in recovery.Discover())
        {
            RecoveryMetadata? meta = await recovery.ReadMetadataAsync(sessionId, ct).ConfigureAwait(false);
            string language = meta?.Language ?? "auto";

            // Replay the PCM through the standard pipeline.
            var session = new RecordingSession(sessionId, language, _backend.CreateStream(language));
            await foreach (float[] frame in recovery.ReadPcmFramesAsync(sessionId, ct).ConfigureAwait(false))
            {
                session.Feed(frame, 16000);
                worker.SignalLive(session);
            }
            session.Stop();
            worker.SignalFinalize(session);
            // Let the worker drain; the session moves to Completed on its own.
            await WaitUntilAsync(() => session.State is SessionState.Completed or SessionState.Faulted,
                ct, timeoutMs: 120000).ConfigureAwait(false);

            if (session.State == SessionState.Completed)
            {
                var entry = new HistoryEntry(
                    session.Id,
                    session.CreatedAt,
                    session.StoppedAt,
                    session.AudioSecondsFed,
                    "Completed",
                    session.Language,
                    session.FinalTranscript,
                    IsCanceled: false,
                    WasPasted: false,
                    WasCopied: false);
                await history.AppendAsync(entry, ct).ConfigureAwait(false);
                recovery.DeleteRecoveryFiles(sessionId);
                recovered++;
            }
            else
            {
                _log.LogError("Recovery of {Id} faulted: {Fault}", sessionId, session.Fault?.Message);
                Violate($"recovery: session {sessionId} faulted during replay");
            }
        }
        return recovered;
    }

    private void Check(bool condition, string name)
    {
        if (!condition)
            Violate(name);
        else
            _log.LogInformation("PHASE3 ok: {Name}", name);
    }
}
