# MetaVoiceType Windows V1 final review

## Result

MetaVoiceType 1.1.0 is implemented, tested, and packaged as a self-contained Windows x64 Avalonia application. The clean-installed application passed the real Parakeet v2, Parakeet v3, CUDA, forced-CPU, Vosk, audio-device, and installer diagnostics described below.

The production path is fully managed application code. It uses established NuGet wrappers and official precompiled runtime binaries; the repository contains no application-authored C, C++, P/Invoke bindings, CMake project, or custom native build.

## Audit and issue resolution

The audit reproduced or confirmed the reported architectural and product problems: Nemotron was the primary dictation engine, CUDA was not selected, command-language selection was conflated with active recognizer state, final command text could leak into dictation, Stop followed immediately by Paste could lose the paste request, the initial onboarding Continue action was broken, light-theme colors were inconsistent, the recording pill and recent-history actions were incomplete, hotkeys were fixed, custom commands and Discord auto-mute were absent, and release packaging was manual/stale.

The resulting V1 resolves them as follows:

- Parakeet v2 is used for English and Parakeet v3 for Automatic/multilingual dictation. Nemotron is no longer a runtime or UI option.
- Audio capture fans out without running Parakeet inference on the capture callback. Sherpa Silero VAD closes speech segments and the ordered decode queue transcribes completed segments. Stop closes only the unfinished tail.
- The runtime detects NVIDIA hardware, verifies the downloaded official Sherpa CUDA runtime, loads the NuGet-provided CUDA/cuDNN dependencies, proves recognizer construction with `provider=cuda`, and otherwise exposes the exact fallback reason before using CPU.
- Vosk command language and Parakeet dictation language are separate settings. English (US) remains the default command listener; Parakeet defaults to `auto`.
- Selected, downloading, installed, and active Vosk language states are distinct. The active recognizer continues listening until the replacement model has verified, extracted, loaded, and atomically swapped.
- Vosk word timestamps identify accepted control-audio spans. Those spans are excluded or cause only affected VAD segments to be re-decoded. Confidence values are parsed only as passive result data and never influence acceptance, ranking, or execution.
- Paste requests are associated with a concrete recording session and can wait on a recording, finalizing, or completed session. Session B may start while session A finalizes.
- The UI now uses semantic dark/light resources, compact status and history layouts, exact per-row Copy/Paste actions, a non-activating recording pill with hover actions, a seven-step gated onboarding flow, model progress/status, diagnostics, and concise copy.
- The toggle-recording shortcut is parsed, validated, persisted, and re-registered immediately. Custom Program, PowerShell, Command Prompt, and Keyboard Shortcut voice commands are language-scoped, duplicate-checked, and execute without elevation.
- Optional Discord auto-mute uses a managed local-RPC abstraction. It preserves an already-muted state, restores only a mute it applied, spans overlapping recording transitions, and never blocks recording when Discord is unavailable.
- `Directory.Build.props` is the sole version source. A push to `main` creates a version tag/release only when it does not already exist; ordinary CI remains build/test only.

## Models and installation

The strongly typed `System.Text.Json` catalog is loaded and validated at startup. It contains artifact identity, official URL, archive type, expected directory, required files, hashes, byte estimates, capabilities, and license information—never transient provider state.

| Artifact | Bytes | SHA-256 | Required content |
| --- | ---: | --- | --- |
| Parakeet TDT 0.6B v2 INT8 | 482,468,385 | `157c157bc51155e03e37d2466522a3a737dd9c72bb25f36eb18912964161e1ad` | encoder, decoder, joiner, tokens |
| Parakeet TDT 0.6B v3 INT8 | 487,170,055 | `5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf` | encoder, decoder, joiner, tokens |
| Silero VAD | 643,854 | `9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6` | `silero_vad.onnx` |
| Sherpa 1.13.5 CUDA 12/cuDNN 9 | 375,615,135 | `2d35c894f1ec4a3b6bed9aaa2b5895394d6afa85c5245a3fd071c8f3d3cae893` | Sherpa API, ONNX Runtime, CUDA/shared providers |

Every download follows `.part` download, SHA-256 verification, traversal-safe extraction, required-file validation, atomic final-directory commit, then initialization. Failed hashes or unsafe archives never become installed models.

Parakeet v3 capability metadata reflects its full published 25-language set; the smaller 12-language list in the Vosk catalog is intentionally the approved command-language UI overlap. These are not treated as the same capability.

## Real runtime evidence

All runs below used the final application from a fresh silent install at `artifacts/qa/installed-v1-release/current/MetaVoiceType.exe`, not a development output.

| Run | Result |
| --- | --- |
| Parakeet v3 Automatic / CUDA | `Parakeet v3 · GPU`, NVIDIA GeForce RTX 4060 Laptop GPU, Sherpa 1.13.5; exact transcript: “This is a real GPU transcription test. Start recording.” |
| Parakeet v2 English / CUDA | `Parakeet v2 · GPU`; exact same expected transcript |
| Parakeet v3 forced CPU | `Parakeet v3 · CPU`; explicit reason `CPU was forced by diagnostics`; exact same expected transcript |
| Vosk English commands | Exact `start recording` command emitted from the same audio fixture |
| Audio hardware | Headset Microphone (Realtek Audio) and Microphone Array (AMD Audio Device) both opened and produced 16 kHz mono frames |

The complete installed diagnostic process took 8,284.5 ms for v3 CUDA, 6,681.0 ms for v2 CUDA, and 4,915.1 ms for forced CPU. These are cold end-to-end process/model/Vosk initialization timings, not isolated inference benchmarks, so they should not be read as a CPU-versus-GPU throughput comparison.

In a real spoken Stop/Paste run, Vosk accepted Stop, the final tail completed in 1,326.1 ms, a subsequent Paste request targeted that recording, and Ctrl+V completed once in 5.2 ms. A deterministic blocked-finalizer test separately proves Stop → Paste during finalization produces exactly one paste of session A while preserving the ability to start session B.

Command-audio trimming is covered at the audio/segment level: a recognized control span overlapping a completed segment invalidates that segment, splits around the control range, and sends only retained speech to the ASR backend. The textual cleaner is used only when timing metadata is unavailable and only at an accepted tail boundary.

## Feature acceptance

- Voice model switching: active-listener preservation and post-initialization atomic swap are implemented; download progress, cancel/retry, selected, and active states are distinct.
- Custom commands: Program, PowerShell, Command Prompt, and Keyboard Shortcut types implemented with managed process/input APIs. Tests verify output, working directory, language scoping, duplicate rejection, and exact one-shortcut execution.
- Discord: protocol/abstraction and UI are complete; fake-integration tests cover initially muted, initially unmuted, overlap, restore, and failure tolerance. End-to-end authorization remains dependent on an owner Discord application and Discord-approved RPC voice scopes; no client secret is embedded.
- Themes: main, settings, models, onboarding, and pill use semantic theme resources; dark and light construction/layout tests pass and both themes were visually exercised.
- Onboarding: Welcome Continue advances to Voice Command Language in the mandatory headless click test. Model and recognizer readiness gate later steps.
- Pill: compact non-activating layout, elapsed time, microphone level, Copy, and hover-only Paste/Stop/Cancel actions.
- History: compact rows with exact-entry Copy and Paste, rather than implicitly acting on the newest entry.
- Hotkey: configurable recording toggle with validation, reset, persistence, immediate replacement, and retention of the old binding if registration fails.
- Recovery/history: PCM remains authoritative until durable recovery transcription commit; normal audio is removed after completion; newest 100 transcripts are retained.
- Releases: main-branch version changes build/test/package a self-contained `win-x64` Velopack release and create `v<Version>` once; `workflow_dispatch` remains available.

## Automated and manual verification

Release build completed with 0 warnings and 0 errors. The deterministic suite passed 36/36 tests with 0 failures and 0 skips. Coverage includes catalog validation, confidence-independent Vosk matching, timestamps, Unicode fallback, safe atomic downloads, PCM/VAD/session ordering, control-span removal, Stop→Paste, overlapping sessions, paste deduplication, custom shells/shortcuts, Discord state, recovery, storage/history limits, managed-code policy, XAML layout, and the actual onboarding Continue click.

Manual/runtime QA included onboarding navigation, main/settings/model views, dark/light rendering, the floating pill, voice-command start/stop/paste, exact paste into a focused Windows window, new recording while prior finalization completed, Parakeet v2/v3 switching, CUDA and forced CPU, two microphones, model downloads with verified hashes, Vosk recognition, fresh installer execution, and installed-app diagnostics.

## Known issues

- The installer is unsigned because no owner code-signing certificate was available. Windows reputation warnings are expected until it is signed.
- Discord auto-mute cannot be authorized end-to-end without an owner Discord client configuration and approval for RPC voice scopes. The rest of the application is independent of it.
- Bundling managed NuGet CUDA/cuDNN runtime dependencies makes this first testable installer approximately 1.62 GB. Parakeet, Vosk, VAD, and the official Sherpa CUDA provider are still downloaded separately and are not embedded model weights.
- Hardware behavior beyond the tested RTX 4060 Laptop GPU and two listed microphones should be expanded through beta feedback; unsupported CUDA environments fall back to CPU with diagnostics.

## Final artifacts

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| `artifacts/releases/MetaVoiceType-win-Setup.exe` | 1,623,916,881 | `8EBEB0DC55466F4326B900EAB6507C9D4D9EA8371DC84657EB93F1EB93B37D15` |
| `artifacts/releases/MetaVoiceType-1.1.0-full.nupkg` | 1,619,362,641 | `2A113EF169B74AA7E2DF0DA9CC9AD493AA51DD4449CCC77D2D35F1DE8FE5F014` |
| `artifacts/releases/MetaVoiceType-win-Portable.zip` | 1,619,269,205 | `0E5112B41A6AFA22DD244253B0CD60C923B60EDAAF51AAFA0B4E0481B12A51A2` |

The primary owner-test artifact is `artifacts/releases/MetaVoiceType-win-Setup.exe`.
