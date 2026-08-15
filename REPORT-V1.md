# MetaVoiceType Windows V1 Report

## Overall result

MetaVoiceType V1 is implemented as a self-contained Windows x64 Avalonia application and packaged with Velopack. It provides local always-on Vosk command recognition, on-demand streaming Nemotron dictation, background finalization, transactional clipboard paste, recovery audio, retained history, onboarding, settings, tray behavior, a floating recording pill, diagnostics, and update checks.

The installable artifact is `artifacts/MetaVoiceType-Setup.exe`; it is 96,749,807 bytes with SHA-256 `AFA90F98D5EDDA30A21CC1FDF2BB61EA562F1774DF6D97562B3AE745F37EAE0C`. The directly runnable publish is in `artifacts/final-publish`.

## Dependencies

Exact direct NuGet versions:

| Package | Version |
| --- | ---: |
| Avalonia.Desktop | 12.1.1 |
| Avalonia.Themes.Fluent | 12.1.1 |
| Avalonia.Fonts.Inter | 12.1.1 |
| Material.Icons.Avalonia | 3.0.2 |
| CommunityToolkit.Mvvm | 8.4.2 |
| Microsoft.Extensions.Hosting | 10.0.10 |
| System.CommandLine | 2.0.10 |
| NAudio | 2.3.0 |
| Vosk | 0.3.38 |
| org.k2fsa.sherpa.onnx | 1.13.4 |
| SharpHook | 7.1.3 |
| TextCopy | 6.2.1 |
| Serilog.Extensions.Hosting | 10.0.0 |
| Serilog.Sinks.File | 7.0.0 |
| SharpCompress | 0.50.1 |
| Velopack | 1.2.0 |
| xunit.v3 | 3.2.2 |
| Microsoft.NET.Test.Sdk | 18.8.1 |
| Avalonia.Headless.XUnit | 12.1.1 |

The repository contains no application-authored native code, native bindings, or manually provisioned native runtime tree.

## Transcription and acceleration

- Engine: NVIDIA Nemotron 3.5 ASR Streaming 0.6B, 560 ms int8 release, through the official sherpa-onnx NuGet wrapper.
- Acceleration: CPU. A clean supported Windows CUDA path was not available through the stable managed/NuGet sherpa package; no custom native workaround was added.
- Provider selection is runtime state and does not appear in the model catalog.
- The verified archive is 475,271,763 bytes with SHA-256 `c6bf5e0df765f9d5b43bc9e0536d4b4b3e7d40bdf5ecf13e45f134c51c05ae3a`.
- Real synthetic-speech transcription succeeded for English, Spanish, Russian, and Ukrainian. Automatic language and forced locale paths were exercised.
- Ten-minute live streaming finalization took 17.6 ms. A recovered three-second crash fixture finalized in 392.4 ms, including recovery replay.

## Reliability results

- Live 10-minute production-path microphone run: 59,283 frames, peak capture dispatch queue 6, zero lost frames, drained queue, 17.6 ms stop tail, and 115,163,136-byte working-set increase including loaded Vosk and Nemotron models.
- Startup regression: model construction no longer holds the session lock; a 12-second launch showed no backlog warning.
- Multi-session overlap, ten rapid sessions, isolation, tail cleanup, retention at 100, atomic storage, duplicate paste, cancel paste, and recovery close-before-delete are covered by deterministic tests.
- Controlled crash verification: process terminated while real microphone PCM was being written; one recovery directory remained. On relaunch it was transcribed into a 332-byte atomic history file and the recovery directory count returned to zero.
- Two active Windows microphones were enumerated, opened, switched, and verified to produce frames.
- The 10-minute ambient run emitted three configured commands. Because the audio was not annotated and the command phrases were discussed during testing, this is recorded as an observed trigger count rather than a false-positive rate.

## Vosk commands and languages

Thirty command-language entries are included: `en-us`, `en-in`, `zh-cn`, `ru`, `fr`, `de`, `es`, `pt-br`, `tr`, `vi`, `it`, `nl`, `ca`, `fa`, `ar-tn`, `kk`, `uk`, `sv`, `ja`, `eo`, `hi`, `cs`, `pl`, `uz`, `ko`, `gu`, `tg`, `te`, `ky`, and `ka`. All 30 archive URLs returned HTTP 200 during catalog validation.

All six phrases are configurable and stored per command language. Blank, duplicate, and `[unk]` phrases are rejected. Applying changes rebuilds the lightweight recognizer without reloading the Vosk model; reset affects only the selected language.

Vosk confidence values are ignored completely. Result alternatives are examined in publisher order and matched only by normalized configured phrases.

The official C# binding marshals runtime grammar text through an ANSI path on Windows. ASCII phrases use restricted grammar. Non-ASCII phrases use unrestricted model decoding followed by exact configured-phrase matching, without custom P/Invoke. Real Russian and Ukrainian command audio emitted the exact configured commands under this fallback.

## Clipboard, UI, installer, and updates

- The production TextCopy-to-SharpHook Ctrl+V transaction was confirmed by the owner in a focused Windows application and measured at 78.8 ms for 33 characters. The clipboard retained the exact requested value.
- Completed UI: six-step onboarding, command-listening/recording distinction, live transcript, recent history, settings, per-language commands, microphone choice, model downloads/progress, dark/light/system theme architecture, floating no-activation recording pill, tray open/exit, and first-close explanation.
- Headless Avalonia construction/layout coverage passes, and the final dashboard was visually inspected at 1920x1080 without clipping or overflow.
- Update checks and user-approved download/restart use Velopack with GitHub Releases. Updates are never silently installed.
- The final self-contained Velopack setup was clean-installed to an isolated directory and its installed launcher passed `--self-test --diagnostics`.

## Test summary

- Deterministic suite: 21 passed, 0 failed, 0 skipped.
- Real model checks: Nemotron installation/SHA/initialization and English, Spanish, Russian, Ukrainian audio.
- Real Vosk checks: English restricted grammar plus Russian and Ukrainian Unicode fallback.
- Real hardware checks: two microphones, live ten-minute run, startup queue regression, paste injection, and crash recovery.

## Known issues

- The local V1 setup is unsigned because no owner code-signing certificate was available. Windows may display a reputation warning.
- Nemotron uses CPU in V1; CUDA was not forced through unsupported native provisioning.
- A 60-minute unattended run was not performed; the required ten-minute streaming run passed losslessly. The diagnostic supports longer runs with `--stress-minutes`.
- Cross-application paste was owner-confirmed in one focused application in this run. Notepad automation was unreliable because the current Windows Notepad process exposed no focusable main-window title; browser, Discord, Office, and IDE smoke checks remain useful owner QA targets.

## Artifacts to test

- Installer: `artifacts/MetaVoiceType-Setup.exe`
- Velopack release set: `artifacts/final-releases`
- Direct publish: `artifacts/final-publish/MetaVoiceType.exe`
- Visual QA captures: `artifacts/ui-final.png` and `artifacts/paste-search.png`
