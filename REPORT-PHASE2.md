# Phase 2 Report — Multi-Session Streaming Architecture

Date: 2026-08-15 · Machine: RTX 4060 Laptop GPU (8GB VRAM), Windows, driver 610.88

## 1. Architecture implemented

```
IAudioSource (mic / WAV)
      ↓ frames (bounded channel, FullMode=Wait — no silent drops)
CapturePump (never runs inference)
      ↓ feed + SignalLive
SessionCoordinator  ← one active RecordingSession, N older sessions
      ↓ StopActive(): detach slot + queue finalize (returns immediately)
DecodeWorker (single thread, fair FIFO, live polls prioritized)
      ↓ serialized IsReady/Decode/GetResult
IAsrBackend (SherpaAsrBackend: one long-lived OnlineRecognizer)
      ├── OnlineStream A (finalizing)
      ├── OnlineStream B (recording)
      └── ...
```

Key invariants:
- **One OnlineRecognizer for the process lifetime.** Model loads once; every recording gets its own OnlineStream.
- **No global busy state.** `Recording`, `Finalizing`, `Completed`, `Faulted` are per-session states; the coordinator has only an "active slot" that frees the instant a stop is requested.
- **All recognizer decode calls are serialized** on the DecodeWorker thread. Live polls are FIFO-prioritized so an old session's drain can never starve the current recording.
- **Microphone callback never performs inference** — it only copies PCM into a bounded channel with `FullMode.Wait` (blocks rather than drops when the consumer falls behind; dropping only happens if the channel is closed).
- **Session-scoped cancellation.** The worker has its own lifetime token; sessions carry no shared token; a fault in A marks A Faulted and never touches B.

## 2. Files modified

```
MetaVoiceType.ConsolePrototype/
  IAsrBackend.cs            (new — IAsrBackend / IAsrStream abstraction)
  SherpaAsrBackend.cs       (new — OnlineRecognizer adapter, one per process)
  RecordingSession.cs       (new — explicit per-session state machine + metrics)
  DecodeWorker.cs           (new — serialized decode worker, fair FIFO)
  SessionCoordinator.cs     (new — active slot + background finalize queue)
  CapturePump.cs            (new — frame → session feeder, never infers)
  Phase2Harness.cs          (new — overlap/stress/fault/long-run test harness)
  PrototypeApp.cs           (updated — routes --phase2/--unit-tests, WAV/mic paths use new architecture)
  Options.cs                (updated — --phase2, --unit-tests flags)
  MicrophoneAudioSource.cs  (updated — FullMode.Wait + max-depth metric + loud drop logging)
  WavFileSource.cs          (updated — real-time pacing for realistic queue dynamics)
  Testing/
    FakeAsrBackend.cs       (new — deterministic fake for unit tests)
    UnitTests.cs            (new — 6 pure-logic unit tests)
  deploy-cuda.ps1           (updated — also overwrites runtimes/win-x64/native)
docs/MANAGED-CODE-POLICY.md (new — repository rule)
```

Removed: `TranscriptionEngine.cs`, `TranscriptionSession.cs`, `ConcurrencyProbe.cs` (superseded by the above).

## 3. Session lifecycle

```
Recording ──Stop()──→ Finalizing ──drain complete──→ Completed
                          └────── exception ────────→ Faulted
```

- `RecordingSession` owns its ASR stream, per-session timing, partial/final text, and terminal state.
- `Feed()` is only legal while `Recording` (throws otherwise — catches misrouted frames during tests).
- `Stop()` transitions Recording→Finalizing; the coordinator's `StopActive()` detaches the slot first, then calls `Stop()` and queues finalization.
- Finished streams are disposed by the worker immediately after terminal state, guarded against double-dispose.

## 4. Decode worker design

- Single `Channel<DecodeWork>` (unbounded), single reader loop.
- Work kinds: `LivePoll` (check/decode one live session) and `Finalize` (drain one stopped session to completion).
- FIFO order with live-priority semantics: finalize work never pre-empts queued live polls; in practice the drain is short (single-digit ms after streaming) so fairness is sufficient.
- Defensive drain cap (10,000 iterations) converts a misbehaving native stream into a Faulted session instead of a hung worker.
- Worker metrics: queue depth, max observed depth, last decode step duration, live polls, finalize drains.

## 5. Synchronization strategy

- **Capture side**: bounded channel (capacity 64 frames ≈ 1.3s at 20ms frames), `FullMode.Wait` — capture thread blocks briefly instead of discarding speech. Drop counter still exists and logs at Error level if it ever fires.
- **Coordinator**: `lock` around the active-slot pointer; slot detach happens before any finalize work is queued.
- **Worker**: single-threaded by construction; per-session metrics use lock-free writes (session state is only mutated by the worker, or by the capture thread during Recording).
- **No shared CancellationToken between sessions.** Application lifetime token (Program), worker lifetime token (DecodeWorker CTS), and per-test tokens in the harness.

## 6. CUDA behavior with multiple streams

The single CUDA OnlineRecognizer served two concurrent streams (A finalizing, B recording) with **zero contention artifacts**: max decode step 0.60ms, max queue depth 37 (out of unbounded), no exceptions, no cross-stream text. GPU utilization stayed moderate (the 560ms chunk model on an RTX 4060 Laptop is small enough that per-chunk decode steps are sub-millisecond), and finalization of an old stream while a new one records did not produce any observable stall in the new stream.

CUDA provisioning note: the .NET native loader probes `runtimes/win-x64/native` BEFORE the app root, so `deploy-cuda.ps1` now overwrites both locations. Without this, the CPU-only NuGet runtime silently wins and sherpa logs "Please compile with -DSHERPA_ONNX_ENABLE_GPU=ON" while falling back to CPU.

## 7. Timing results

| Metric | Value |
|---|---|
| Stop(A) → B accepts first frame | **46.7 ms** |
| A finalization (queued→completed) | **87.8 ms** (max across all sessions) |
| Max single decode step (IsReady+Decode+GetResult) | **0.60 ms** |
| Model load (CUDA) | ~5.4 s (one-time) |
| 586 s continuous session finalization | ~4 ms (streaming already did the work) |

The 46.7ms transition includes the WAV source's real-time pacing granularity and pump startup — the coordinator's slot-detach itself is sub-millisecond (verified in unit tests with the fake backend).

## 8. Queue-depth results

- Decode queue max observed depth: **37** items (transient; drained immediately).
- Audio queue max depth: n/a for WAV harness (pump consumes instantly); mic path metrics retained for the live harness.
- No queue growth over the 586s long-run: depth oscillated near zero throughout.

## 9. Memory/VRAM behavior

- VRAM: ~1.6 GiB with model + active streams (same footprint as Phase 1 single-stream; concurrent streams add negligible memory).
- Managed memory stable across the 586s run and 8 sessions (no leak observed; streams are disposed at terminal state).

## 10. Dropped-frame count

**0** across all tests (overlap, stress chain, fault isolation, 586s long-run). The mic path's `FullMode.Wait` channel plus the WAV pump's direct feed never hit the drop path; the drop counter remains as an Error-level safety net.

## 11. Transcript-isolation test

- Overlap (es + de): A="preguntes", B="hat ein En" — no cross-language words in either transcript.
- Stress chain (es/de/fr/uk): all six sessions Completed with language-correct text (es="preguntes", de="hat ein En", fr="Ne vous demandez pas…", uk=Cyrillic) and no session containing another session's signature phrase.
- Per-stream language config (`SetOption("language", ...)`) was verified across four languages on concurrent/follow-on streams.

## 12. Fault-isolation test

Injected a stream fault into session A's finalize drain:
- A → **Faulted** with exception captured.
- B (started immediately after A's stop) → **Completed** with its own transcript.
- Worker stayed alive; queue drained normally afterward.
- Unit test additionally proves a fault during A's live phase leaves B recording uninterrupted.

## 13. Problems encountered

1. **CAS spin-loop deadlock** in the initial depth-metric implementation (`max` never refreshed inside the loop) — fixed; also hardened the pattern in MicrophoneAudioSource.
2. **Fake stream semantics** for unit tests initially modeled `IsReady` incorrectly (stayed true forever after `InputFinished`), which masked as a hang — fixed to mirror sherpa's drain semantics.
3. **.NET native loader probing order**: `runtimes/win-x64/native` shadows app-root DLLs, silently falling back to CPU. `deploy-cuda.ps1` now deploys to both locations. This cost the most debugging time.
4. **560ms chunk latency floor**: transcripts of recordings shorter than ~1.5s are often empty (the model needs a full chunk plus context). Tests were adjusted to feed ≥1.5s; the product implication is that very short recordings may produce empty text — acceptable for v1, worth documenting.
5. **Harness initially used direct `session.Stop()`** instead of the coordinator's `StopActive()`, bypassing the slot-detach path the tests were meant to prove. Corrected so the harness exercises the production path.

## 14. Recommended Phase 3 architecture

Phase 2 proved the concurrency core. Phase 3 (durable temporary recovery) should build on it as follows:

1. **Recovery writer as a CapturePump observer**: subscribe to the same `onFrame` hook (already in CapturePump) to append PCM to a per-session temp file. No new capture path.
2. **Session state persistence**: serialize `RecordingSession` metadata (Id, language, timestamps, state) to JSON in `%AppData%/MetaVoiceType/sessions/`; the in-memory state machine stays the source of truth while running.
3. **Recovery on startup**: scan temp audio dirs; sessions left `Recording`/`Finalizing` at last shutdown are replayed through the same DecodeWorker pipeline used in Phase 2 (a recovery session is just a `Finalize` work item fed from a WAV file).
4. **Atomic commit then delete**: write transcript JSON, fsync, rename into place, then delete the temp audio. Never delete audio before the transcript file exists on disk.
5. **Retention**: 100-entry ring buffer on the history store; delete oldest entries' text+metadata together.

Do NOT implement Vosk (Phase 4) or UI (Phase 7+) yet — the recovery path must be proven at the console level first.
