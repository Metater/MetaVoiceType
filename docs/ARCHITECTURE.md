# Architecture

MetaVoiceType is a Windows-only .NET 10 Avalonia application using dependency injection, CommunityToolkit.Mvvm, and managed wrappers for native-backed speech/input libraries. It contains no application-authored native code or bindings.

## Lossless audio and pre-roll

`WindowsAudioCaptureService` keeps the WASAPI callback to copy, timestamp, enqueue, and return. Conversion, Vosk, VAD, Parakeet, recovery I/O, FFT, JSON, and UI dispatch all happen outside that callback. The managed capture channel is bounded at 3,000 frames and uses non-blocking writes; captured, dispatched, dropped, current queue, and high-water counters expose actual behavior. A normal run must remain at zero drops.

Each 16 kHz frame receives a global sample range and enters a rolling one-second `AudioPreRollBuffer` before Vosk processes it. When Start or Continue is accepted, Vosk's word end timestamp is mapped onto that sample clock. Only buffered samples after the command boundary are replayed, through the exact live join boundary. The frame that caused recognition is not delivered twice. If a control command occurs during recording, its timestamped range is removed before Parakeet decode; Vosk confidence is never used.

Silero VAD produces speech segments. `DecodeCoordinator` serializes Parakeet jobs and reports its queue high-water mark. `RecoveryWriter` independently persists PCM and its logical/segment identity, with its own queue instrumentation.

One `AudioSpectrumService` subscribes to dispatched frames, maintains a 2,048-sample ring, and coalesces FftSharp transforms to 30 FPS only when new audio exists. It applies a Hanning window, logarithmic 80 Hz–4 kHz buckets, a -75 dB floor, normalization, and asymmetric attack/release smoothing. The same immutable 20-bar frame feeds the main view and pill.

## Logical transcripts

A normal Start creates a new logical transcript. Continue selects the newest eligible record and starts a new physical segment carrying the original logical ID, original start time, prior corrected text, segment count, and accumulated duration. Completion upserts that logical history record; repeated Continue operations therefore remain one item. Canceling a continuation preserves the original record. Recovery metadata reconnects an interrupted continuation to that same logical ID.

Accepted command audio is removed first. `WordReplacementEngine` then applies literal, case-insensitive, Unicode-aware boundary matching in longest-match-first order to the new authoritative segment only. Corrected prior history text is never processed twice. The combined corrected result is used for history, automatic copy, Recent Copy, pill snapshots, and Paste Recording.

## Commands and lifecycle shortcuts

Vosk grammar contains seven configurable built-ins plus enabled custom commands for the active command language. Program, PowerShell, Command Prompt, and keyboard actions remain available while dictating. `ShortcutGestureParser.ParseAction` permits single non-modifier keys; playback is modifier-down → key-down → key-up → reverse-modifier-up, with a `finally` release guard.

`RecordingEventShortcutPlayer` owns recording lifecycle deduplication. A shortcut fires once after a session truly starts and once on every actual end path (Stop, Paste Recording, Cancel, toggle, or clean exit). It has no target-app state and no Discord API dependency.

## Models, providers, and storage

The strongly typed JSON catalogs contain immutable artifact identity, repository/release/asset pins, URL, exact bytes, SHA-256, extraction type, required files, capability, and license metadata—never current CPU/GPU state. Downloads use `.part → byte validation → SHA-256 → safe extraction → required-file validation → atomic commit`. Replacement Vosk models initialize before becoming the active language; the old recognizer remains active during download and validation.

At runtime, Sherpa prefers the official pinned CUDA bundle when a compatible NVIDIA GPU is detected and cleanly recreates the recognizer on CPU after any provider failure. Parakeet V3 automatically detects among its 25 supported languages; this backend exposes no fake forced-language hint.

`%LOCALAPPDATA%\MetaVoiceType` retains compatible V1.1/V1.2 settings/history/model locations. Schema 4 migrates single phrases to alias arrays and flat replacements to groups without changing theme, hotkey, custom actions, or installed models. History loads are normalized and rewritten in UTC; presentation converts to the current Windows time zone. `METAVOICETYPE_DATA_ROOT` provides an isolated root for diagnostics and QA only.
