# MetaVoiceType V1.2 final report

## 1. V1.2 result

MetaVoiceType 1.2.0 is implemented, Release-built, runtime-tested, visually inspected, packaged, and clean-installed as a self-contained Windows x64 Avalonia application. The installed binary reports file version `1.2.0.0` and passed real Parakeet V3 CUDA and Vosk recognition.

The application remains managed C# code. Audio, Vosk, Sherpa-ONNX, CUDA dependencies, input simulation, UI, archive extraction, clipboard, and updates use established NuGet packages or verified publisher binaries; there is no application-authored C/C++, CMake, P/Invoke wrapper, or native build.

## 2. Implemented requested features

- Deterministically pinned dictation, VAD, CUDA-runtime, and Vosk artifacts.
- Real byte and percentage download progress with cancel/retry and truthful selected/downloading/installed/active states.
- System theme as the fresh-install default, plus explicit Dark and Light.
- Literal, case-insensitive, boundary-aware, longest-first word replacements.
- Single-key and modified custom keyboard actions with exact key-down/key-up ordering.
- Generic recording-start and recording-end shortcuts; the Discord RPC/SDK abstraction was removed.
- Dynamic active-language command copy and the `Paste Recording` naming.
- Built-in `Continue Recording`, one logical history item across repeated continuation, and continuation-aware recovery.
- Sample-positioned one-second command pre-roll without gaps or duplication.
- Non-blocking, bounded capture dispatch and captured/dispatched/dropped/high-water diagnostics.
- Compact Recent Copy/Delete actions with confirmation.
- Stable recording pill with Copy, Paste, Stop, and Cancel; no meter and no hover expansion.
- Models/settings/about polish, upstream credits, V1.1 migration, and version-driven application releases.

## 3. Exact model artifact pins

| Artifact | GitHub asset ID | Bytes | SHA-256 |
| --- | ---: | ---: | --- |
| Parakeet TDT 0.6B v2 INT8 | 283097678 | 482,468,385 | `157c157bc51155e03e37d2466522a3a737dd9c72bb25f36eb18912964161e1ad` |
| Parakeet TDT 0.6B v3 INT8 | 283097583 | 487,170,055 | `5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf` |
| Silero VAD | 271935959 | 643,854 | `9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6` |
| Sherpa-ONNX 1.13.5 CUDA 12/cuDNN 9 runtime | 509879675 | 375,615,135 | `2d35c894f1ec4a3b6bed9aaa2b5895394d6afa85c5245a3fd071c8f3d3cae893` |

All twelve Vosk entries separately pin publisher model name, URL, archive size, SHA-256, expected directory, required files, license, language, and default phrases. Those exact values are recorded in `docs/MODELS.md` and `voice-command-languages.json`.

## 4. Parakeet versions

- `Automatic` uses multilingual Parakeet TDT 0.6B V3 INT8. Its 25 published languages and automatic detection are cataloged; no fake forced-language hint is exposed.
- `English` uses Parakeet TDT 0.6B V2 INT8.
- Both run through Sherpa-ONNX 1.13.5. Vosk command language remains an independent setting.

## 5. GPU status

Real final-hardware diagnostics selected `provider=cuda` on an NVIDIA GeForce RTX 4060 Laptop GPU with Sherpa runtime 1.13.5. V2 and V3 both returned `Parakeet · GPU`. A forced-CPU V3 run returned `provider=cpu` and the same expected transcript.

## 6. Model-download progress

The generic model manager executes:

```text
catalog validation -> temporary .part -> byte progress -> expected-size check
-> SHA-256 check -> traversal-safe extraction -> required-file check
-> atomic directory commit -> backend initialization -> active state
```

It rejects missing hashes, non-positive expected sizes, mismatched content length, hash failures, traversal entries, missing files, and malformed catalogs. Vosk retains the current listener while a replacement downloads and changes the active label only after load, grammar setup, and initialization.

## 7. Word replacements

Rules are literal rather than regular expressions, Unicode-aware, case-insensitive, boundary-aware, ordered longest match first with deterministic ties, and preserve replacement text exactly. They run only on the new authoritative Parakeet segment after command-audio exclusion and before history/copy/paste; already-corrected continuation text is not processed twice. Tests cover punctuation, beginning/end positions, repeated mixed-case matches, substrings, overlapping phrases, Unicode, exact replacement casing, and invalid empty matches.

## 8. Custom shortcut commands

Custom actions support Program, PowerShell, Command Prompt, and Keyboard Shortcut. Action shortcuts accept individual keys such as `Enter`, `Escape`, and `F5`, as well as combinations. Playback presses modifiers, presses/releases the action key, then releases modifiers in reverse order, with a `finally` release path. The global recording hotkey still requires a modifier. The UI records and validates shortcuts and warns when a voice phrase is unusually short/common.

## 9. Recording-event shortcuts

Optional start/end shortcuts are generic settings. A session-ID deduplicator fires start exactly once after recording actually begins and end exactly once for Stop, Paste, Cancel, Continue transitions, and clean application exit. Failures are logged and cannot block recording.

## 10. Discord keybind example

To toggle Discord mute, create a Discord `Toggle Mute` keybind and record that same shortcut in both MetaVoiceType event fields. MetaVoiceType only plays the shortcuts; it does not read Discord state. The same fields can control any application. No Discord SDK, RPC, client secret, or Discord-specific runtime code remains.

## 11. Continue Recording architecture

Continue selects the newest completed/canceled transcript with text, starts a new physical session linked to the same logical transcript ID, prepends the stored corrected text, and upserts the result. Repeated Continue remains one history row while accumulating segment count and total duration. Normal Start always creates a new logical ID. Copy/Paste use the combined text. Cancel preserves the prior logical text. With no suitable item, the command reports `Nothing to continue` without starting capture.

## 12. Pre-roll

`AudioPreRollBuffer` retains approximately one second by absolute sample position. A Vosk match supplies its timestamp-derived command end sample; Start and Continue replay only samples after that boundary through the current capture position and skip replayed live data. The join is sample-exact, so immediate post-command speech is retained once with neither a gap nor duplication.

## 13. Dropped-frame metrics

The microphone callback only copies PCM and calls `TryWrite` on a finite 3,000-frame channel; conversion and subscribers run on the dispatch task. Exhaustion increments a dropped counter and emits a critical log rather than blocking capture. Captured, dispatched, dropped, current depth, capture high-water, Parakeet high-water, and recovery high-water are reported.

The 20-minute real-microphone CUDA soak produced:

```text
FramesCaptured=120104
FramesDispatched=120104
FramesDropped=0
CaptureQueueHighWaterMark=6
ParakeetQueueHighWaterMark=1
RecoveryQueueHighWaterMark=10
```

The exact final bounded-buffer binary then completed a one-minute smoke at 6,103 captured/dispatched, zero dropped, with high-water marks 2/1/2. All queues drained.

## 14. Recent UI

Recent rows are compact and expose only Material Copy and Delete actions. Copy targets the exact row. Delete shows a confirmation and removes the matching logical transcript and metadata. Continuations update their existing row.

## 15. Pill

The pill is a stable 420×58 topmost, non-activating window with elapsed time and always-visible Material Copy, Paste, Stop, and Cancel buttons. It has no level meter and no hover resizing. The window surface is transparent and the inner rounded border has a 23px radius and margin; screenshot/pixel inspection confirmed that all four window-corner pixels show the underlying desktop, including the bottom corners.

## 16. Theme/default changes

Fresh settings default to `System`, mapped to Avalonia's default theme variant so it follows Windows. Existing explicit V1.1 Dark/Light choices survive migration. System (Windows dark), explicit Light, onboarding, main/history, settings, icons, and pill were captured and visually inspected. The settings tabs were tightened to remain on one row, and the official Material icon styles are loaded so actions render in both themes.

## 17. Language switching state

Vosk voice-command language and Parakeet dictation language are intentionally distinct. English (US) is the default command language; dictation defaults to V3 automatic detection. During a Vosk replacement download, the selected row shows real bytes/percentage while the prior language stays labeled as current. `Active` changes only after verified install and recognizer/grammar initialization.

## 18. GitHub release workflow

On a push to `main` (or manual dispatch), CI restores, builds, and tests, reads the single version in `Directory.Build.props`, and queries `gh release view vX.Y.Z`. If that release exists, packaging/release creation is skipped. Otherwise it packages 1.2.0, creates the versioned tag/release, and uploads all installer/update artifacts. Model GitHub assets remain independently pinned in catalogs.

## 19. Automated tests

Final Release result: **56 passed, 0 failed, 0 skipped**, with **0 build warnings and 0 build errors**. Coverage includes catalog pins, size/hash/traversal failures, confidence-independent Vosk matching, timestamps, replacement semantics, single-key and modified playback, real managed PowerShell/CMD/program execution, exactly-once lifecycle shortcuts, pre-roll joins, continuation/upsert/delete/recovery, Stop→Paste races, V1.1 migration, bounded capture draining, managed-code policy, XAML construction, and onboarding navigation.

## 20. Manual/runtime tests

- Real microphones: Headset Microphone (Realtek Audio) and Microphone Array (AMD Audio Device) opened and emitted 16 kHz mono frames.
- Real V3 CUDA, V2 CUDA, and forced V3 CPU runs transcribed `This is a real GPU transcription test. Start recording.`
- Vosk recognized `start recording` from the same WAV fixture without using confidence for any decision.
- Twenty-minute real microphone/Vosk/VAD/Parakeet/recovery CUDA soak: zero dropped frames.
- Exact final bounded-buffer build: one-minute real microphone smoke, zero dropped frames.
- System/Dark/Light, onboarding, main/history, settings, Material actions, and recording pill visually inspected.
- Pill corner transparency, stable dimensions, and all four actions visually inspected.
- Clean 1.2.0 silent install completed with exit code 0; installed V3 CUDA/Vosk diagnostic completed with exit code 0.
- Program, PowerShell, Command Prompt, Enter, modified shortcuts, lifecycle events, Continue/repeated Continue, replacements, Recent actions, pre-roll, recovery, and Stop/Paste races were exercised by deterministic executable integration tests.

QA evidence is under `artifacts/qa`, including the soak/smoke logs and `v12-system-settings-window.png`, `v12-light-main-window.png`, `v12-system-onboarding.png`, and `v12-system-pill-padded.png`.

## 21. Known issues

- The installer is unsigned because no owner code-signing certificate was available. Windows reputation warnings are expected until an owner certificate is supplied.
- The self-contained CUDA/cuDNN dependencies make this first testable installer approximately 1.62 GB. Model weights and the publisher CUDA provider remain separately verified downloads rather than embedded model data.
- Hardware coverage is the tested RTX 4060 Laptop GPU and two listed microphones. Unsupported CUDA setups clearly report the fallback reason and use CPU.

No known acceptance-blocking application defect remains.

## 22. Final installer and release artifacts

The clean install used `artifacts/qa/installed-v1.2/current/MetaVoiceType.exe`.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `artifacts/releases/MetaVoiceType-win-Setup.exe` | 1,623,934,997 | `44030812550666FFC384BD6C30BF5B14340FC3ECC4CE725706EF82440824BA32` |
| `artifacts/releases/MetaVoiceType-1.2.0-full.nupkg` | 1,619,380,757 | `94A442A2731B0DBDD53D6110BABE93033438906BAAB04B4130C0206DA6B622B7` |
| `artifacts/releases/MetaVoiceType-win-Portable.zip` | 1,619,287,320 | `6ABE6F025018F8100EC84857A8BD55B374885C6A6B88D8865EEDA3965CFE11BF` |

The primary owner-test artifact is `artifacts/releases/MetaVoiceType-win-Setup.exe`.
