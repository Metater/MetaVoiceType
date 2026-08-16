# Testing

```powershell
dotnet restore MetaVoiceType.slnx
dotnet build MetaVoiceType.slnx -c Release --no-restore
dotnet test MetaVoiceType.slnx -c Release --no-build --no-restore
```

The deterministic suite covers model pins and safe downloads, exact byte/hash rejection, Vosk timestamp matching without confidence decisions, twelve-language-command completeness, multiple aliases, legacy Paste Here, Unicode grammar fallback, grouped literal replacements, UTC/DST display, whitespace suppression, ScrollLock chords, cue signatures/volume, shared spectrum output, explicit paste terminal states, exactly-once recording events, sample-aligned pre-roll joins, logical continuation/upsert/delete-all, V1.1/V1.2 settings migration, command suppression, Stop→Paste races, overlapping sessions, recovery ordering, managed-only policy, and Avalonia construction.

## Windows diagnostics

```powershell
dotnet run --project src/MetaVoiceType/MetaVoiceType.csproj -c Release -- --self-test
```

- `--install-models --dictation-language auto|en --command-language ID`
- `--audio-file PATH [--test-command]`
- `--force-cpu`
- `--list-audio-devices`
- `--stress-minutes 20`
- `--paste-text TEXT`
- `--recovery-crash-seconds N`
- `--reset-onboarding`

The stress command streams a real microphone through Vosk, Parakeet, Silero VAD, recovery I/O, and the shared FftSharp service. Each minute reports queue state; completion reports `FramesCaptured`, `FramesDispatched`, `FramesDropped`, `RecoveryFrames`, `VoskFrames`, `VadFrames`, `SpectrumFrames`, high-water marks, memory, and provider. Normal acceptance requires zero dropped frames, at least one FFT frame, and drained queues.

For isolated onboarding/visual QA, set `METAVOICETYPE_DATA_ROOT` to a temporary directory; production defaults remain `%LOCALAPPDATA%\MetaVoiceType`.

## Manual V1.3 checklist

Inspect onboarding and main/settings/history/dialog/pill states under System, Dark, and Light at wide and 620–820 px widths. Exercise active-language alias copy, a fresh Vosk switch with visible byte progress, Parakeet V2/V3 GPU and CPU-only mode, immediate speech after Start/Continue, Stop/Paste/Cancel, paste/new-recording overlap, repeated continuation and recovery, grouped replacements, every custom action type, Ctrl+Alt+ScrollLock event capture, cue Test/volume, Recent Copy/two-click Delete/Delete All, all four pill buttons, transparent corner pixels, tray/clean exit, update progress, and a clean installer upgrade.
