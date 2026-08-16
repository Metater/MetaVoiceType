# MetaVoiceType V1.3 polish report

## 1. Baseline V1.2 audit

The V1.2 baseline restored and built with zero warnings and zero errors, and all 56 baseline tests passed. The source audit covered capture, Vosk, Parakeet/sherpa-onnx, VAD, sessions, recovery, paste, storage, settings migration, Avalonia views/resources, global shortcuts, catalogs, packaging scripts, and release workflows. V1.3 preserves the bounded fan-out audio architecture, single active dictation session, local-only recognition, recovery WAVs, and Velopack update model.

## 2. Source-level problems discovered

History offsets were not canonicalized; blank transcripts could be persisted; one phrase per command prevented useful aliases; Paste Here remained the primary name; replacements were flat one-to-one entries; paste state was split across booleans; cue routing and gain were inconsistent; shortcut capture targets could interfere; the main layout clipped at smaller widths; recent-item deletion was remote from the item; VAD tail closure was conservative; and there was no real FFT visualization. A late race audit also found that canceling a reserved paste before final text existed could leave it queued; that path now terminates immediately and is regression-tested.

## 3. History UTC/local implementation

`JsonHistoryStore` normalizes both timestamps to UTC on add and load, rewrites legacy offset-bearing records atomically, trims text, and rejects whitespace-only records. `TranscriptTimeFormatter` converts UTC to the current Windows time zone at display time and emits a DST-aware ASCII suffix such as `(UTC-04:00)`. Production history inspection confirmed persisted timestamps use `+00:00`; deterministic summer/winter tests confirm Eastern offsets of `-04:00` and `-05:00`.

## 4. Alias architecture

Schema 4 stores built-in aliases as `language -> command -> string[]` and custom actions as `Aliases`. A reusable alias-list view model/control normalizes, deduplicates, validates, adds, and removes values. The active Vosk grammar receives every configured alias for the selected command language only. Vosk confidence is merely passed through diagnostic result data and is never used for acceptance, ranking, or command choice.

## 5. Paste Recording migration

The primary English command and all product-facing copy now say **Paste Recording**. **Paste Here** remains the default secondary English alias and is accepted by migration and matching. Legacy single-string overrides migrate into alias arrays without losing the user's phrase.

## 6. Grouped replacements

`WordReplacementGroup` maps multiple literal, case-insensitive, boundary-aware matches to one replacement. The new Replacements UI uses the shared alias editor for the match list. QA loaded `calcutta`, `kol kata`, and `call kata` as one group producing `Kolkata`; replacements run before history, copy, and paste.

## 7. Custom Commands redesign

Custom commands now support multiple spoken aliases, language scoping, duplicate/shadow validation, and a clear empty state. The details panel conditionally shows only fields relevant to Program, PowerShell, Command Prompt, or Keyboard Shortcut. Visual QA exercised an `enter` / `press enter` custom action mapped to the single Enter key.

## 8. Cue architecture/fix

Start, Continue, Stop, Paste Recording, Cancel Recording, Cancel Paste, and Copy use one orchestrator-owned cue path regardless of whether the trigger came from voice, the global shortcut, the main window, or the pill. Each built-in has a distinct deterministic cue signature. Error and recovery cues remain separate, and idle shutdown no longer emits an error cue.

## 9. Cue-volume fix

All accepted/error/recovery calls receive the saved cue volume. `GainForVolume` clamps the UI range and maps 0 to silence, 0.5 to 0.08 gain, and 1 to 0.16 gain. The Audio & Models page includes a Test button next to the purple slider; automated tests cover zero, midpoint, and clamp behavior.

## 10. Shortcut capture architecture

One `ShortcutCaptureTarget` coordinator owns capture state for Recording Toggle, Custom Command, Recording Started, or Recording Stopped. Entering one target clears the others, modifier-only input is ignored, and normal toggle handling is suppressed during capture. UI key formatting and SharpHook parsing share canonical names.

## 11. Ctrl+Alt+ScrollLock result

`Scroll` and `ScrollLock` both parse to SharpHook's Scroll Lock key, and Pause and PrintScreen are also supported. `Ctrl+Alt+ScrollLock` round-trips exactly and was loaded independently for both recording-event settings while the global toggle remained `Ctrl+Space`. Event playback is exactly once per session and releases modifiers in reverse order.

## 12. Purple design-system changes

Dark and light dictionaries now define a coherent purple accent, hover, and soft surface while green/yellow/red remain semantic status colors. Avalonia Fluent `ColorPaletteResources` sets accent `#8068FF` in dark mode and `#6550E8` in light mode, fixing native checkboxes, tabs, sliders, and selection indicators rather than styling isolated controls.

## 13. FFT package/version

The implementation uses the established managed NuGet package **FftSharp 2.2.0**, centrally pinned. No custom DSP library or native C/C++ code was added.

## 14. FFT frequency mapping/smoothing

One 2,048-sample ring is windowed with FftSharp's Hanning window and transformed with `FFT.Forward` / `FFT.Power`. Twenty logarithmic bands span 80 Hz to 4 kHz, normalize from -75 dB to -20 dB, and use asymmetric smoothing: 0.55 attack and 0.18 release.

## 15. FFT performance

FFT publication is coalesced to 30 FPS and skipped when no new audio arrived. Main and pill acquire consumer leases; transforms stop when neither visualization is visible. Both views consume the same immutable frame, so no duplicate FFT exists. The microphone soak produced 36,366 spectrum frames without any audio drops or queue growth.

## 16. Zero-drop microphone metrics

The real 20-minute headset-microphone soak used Parakeet V2 on the NVIDIA RTX 4060 Laptop GPU through CUDA. Results: `FramesCaptured=120116`, `FramesDispatched=120116`, `FramesDropped=0`, `RecoveryFrames=120015`, `VoskFrames=120015`, `VadFrames=120015`, and all ending queue depths zero. High-water marks were capture 4, Parakeet 1, and recovery 3. Finalization was 1.2 ms. Memory delta was 200,794,112 bytes; memory fell after an earlier peak and showed no queue-backed runaway.

## 17. VAD old/new configuration

V1.2 used threshold 0.30, minimum silence 0.45 s, minimum speech 0.20 s, maximum speech 20 s, and a 512-sample window at 16 kHz. V1.3 uses threshold 0.25, minimum silence 0.30 s, minimum speech 0.15 s, maximum speech 10 s, and retains the recommended 512-sample/16-kHz window and one thread. Pre-roll and sample-aligned control-span behavior remain intact.

## 18. VAD latency before/after

Quantized tail closure changed from `ceil(0.45 / 0.032) * 32 = 480 ms` to `ceil(0.30 / 0.032) * 32 = 320 ms`: a 160 ms reduction, or 33.3%. Tests pin the new budget and window size, while the soak confirms the more responsive values do not sacrifice the zero-drop invariant.

## 19. Pill/paste-state implementation

`PasteRequestState` is authoritative: Idle, Queued, Preparing, Pasting, Succeeded, Failed, or Canceled. The coordinator reserves deferred paste atomically, rejects duplicates, and always reaches a terminal state, including empty/canceled deferred requests. A blocked paste from recording A can coexist with recording B; deterministic QA verifies B stays recording after A completes. Paste-only pill mode shows a spinner and Cancel Paste; recording mode retains Copy, Paste Recording, Stop, and Cancel. The 438x66 pill never expands. Its native window and root are transparent, its surface is inset and rounded, and screen-captured corner pixels matched the desktop rather than the surface.

## 20. Responsive breakpoints

The main window has a 620x560 minimum. Below 820 content pixels, the command card stacks beneath Live Transcript; above it, the two-column layout returns. Live text scrolls inside a bounded 132-pixel region, action buttons wrap, and status/title text wraps. Visual QA passed at 636x599 actual minimum chrome size, 860x680 medium, and 1056x799 wide.

## 21. Recent-delete UX

Each Recent row has adjacent Copy and Delete controls. The first delete click shows an inline four-second instruction; clicking the same row again deletes it. Delete All uses an explicit inline confirmation and `IHistoryStore.DeleteAllAsync`; neither path affects models or settings.

## 22. CPU-only behavior

GPU remains preferred by default. CPU-only is an opt-in saved runtime setting and `--force-cpu` diagnostic override. Provider choice lives in `SherpaRuntimeBootstrapper`, not catalog metadata. The installed-package self-test initialized Parakeet V3 on CPU successfully; recorded fixtures and the long soak initialized CUDA successfully.

## 23. NVIDIA status branding

Runtime labels now read `Parakeet V2 on GPU` or `Parakeet V3 on GPU` with the requested capital V and no dot separator. A restrained eye-outline mark appears only when the active provider is NVIDIA CUDA. CPU and fallback states use plain descriptive text.

## 24. Model catalog verification

The strongly typed System.Text.Json catalog validates schema, IDs, kinds, HTTPS URLs, archive type, expected directory, SHA-256, estimated bytes, required files, file maps, capabilities, and licenses at startup. Parakeet V2/V3, Silero VAD, and sherpa CUDA 12 pins retain their exact URLs, asset IDs, hashes, and byte counts. Parakeet V3 declares its official 25-language set, automatic detection, `defaultLanguage: auto`, and CC-BY-4.0 license. No provider/acceleration state appears in metadata. Vosk language models and default command strings remain in their separate catalog. This release contains Parakeet rather than a Nemotron artifact; therefore it does not attach incorrect OpenMDW metadata to Parakeet.

## 25. Migration results

Schema 4 migration preserves V1.2 theme, startup, audio device, model choice, cue volume, hotkeys, command language, custom actions, and explicit settings. Legacy built-in overrides become alias lists; legacy custom `Phrase` becomes the first alias; flat replacements group by identical replacement destination; timestamps normalize on history load. Migration clears the obsolete serialized containers after conversion and persists the upgraded form atomically. Installed model directories are untouched.

## 26. Automated test results

Final Release result: **70 passed, 0 failed, 0 skipped**. The solution builds with **0 warnings and 0 errors**. Coverage includes catalogs/download safety, confidence-independent Vosk matching, aliases, replacement grouping, UTC/DST, blank suppression, uncommon chords, cue signatures/gain, spectrum output, paste terminal states, deferred-cancel cleanup, old-paste/new-recording overlap, pre-roll/continuation/recovery, migrations, managed-code policy, Avalonia construction, and responsive/pill declarations.

## 27. Manual QA

The real application was rendered and inspected on Main, General, Voice Commands, Custom Commands, Replacements, Audio & Models, About & Updates, recording pill, and paste-only pill. Dark, Light, and System modes were exercised; purple native controls were verified. Minimum, medium, and wide sizes showed no overlap. English, Spanish, Russian, and Ukrainian recorded command fixtures each activated the selected Vosk catalog independently of Parakeet dictation. English used Parakeet V2/CUDA; multilingual fixtures used Parakeet V3/CUDA. Production history inspection confirmed `+00:00` storage, and local rendering is covered by DST-aware formatter tests. The installed artifact's CPU self-test passed with 101 capture frames, queue high-water 1, callback 0.011 ms, and initialized models.

## 28. Known remaining issues

There are no known V1.3 functional blockers. The locally produced binaries are unsigned, so Windows may show an unknown-publisher/SmartScreen prompt until a release signing certificate is configured. Velopack 1.2.0's setup executable has an upstream command-line parser defect when test-only executable arguments are forwarded after `--`; normal silent and interactive installation work, and installed-app self-test was run directly instead.

## 29. Installer path

Primary installer: `artifacts/releases/MetaVoiceType-win-Setup.exe` (1,623,981,521 bytes), SHA-256 `5E4B9D099ECA2E3FE7E80325E0320C7F51BF6FB5280595D95318CC55FC1032EC`. Portable ZIP and full NuGet release package are alongside it. An isolated smoke installation was completed at `artifacts/install-smoke/v1.3.0`; its installed executable reports file version `1.3.0.0`.

## 30. Release status

**Ready for owner testing.** Version is 1.3.0, Release build/tests/package succeeded, the self-contained win-x64 installer was produced and installed, and the installed binary passed its CPU self-test. The GitHub release workflow remains version-gated: it checks for an existing `v1.3.0` release and packages only when that version tag/release does not already exist, so ordinary pushes do not create duplicate releases.
