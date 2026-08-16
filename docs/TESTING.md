# Testing

```powershell
dotnet restore MetaVoiceType.slnx
dotnet build MetaVoiceType.slnx -c Release --no-restore
dotnet test MetaVoiceType.slnx -c Release --no-build --no-restore
```

The deterministic suite covers model pins and safe downloads, exact byte/hash rejection, Vosk timestamp matching without confidence decisions, twelve-language-command completeness, Unicode grammar fallback, literal replacement boundaries/order/case, empty replacement rejection, single-key and modified shortcut sequences, exactly-once recording events, sample-aligned pre-roll joins, logical continuation/upsert/delete, V1.1 settings migration, command suppression, Stop→Paste races, overlapping sessions, recovery ordering, managed-only policy, and Avalonia construction.

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

The stress command streams a real microphone through Vosk, Parakeet, Silero VAD, and recovery I/O. Each minute reports `FramesCaptured`, `FramesDispatched`, `FramesDropped`, capture/Parakeet/recovery queue depths and high-water marks, memory, and final GPU provider. Normal acceptance requires zero dropped frames and drained queues.

For isolated onboarding/visual QA, set `METAVOICETYPE_DATA_ROOT` to a temporary directory; production defaults remain `%LOCALAPPDATA%\MetaVoiceType`.

## Manual V1.2 checklist

Inspect onboarding and main/settings/history/dialog/pill states under System, Dark, and Light. Exercise active-language command copy, a fresh Vosk switch with visible byte progress, Parakeet V2/V3 GPU and forced CPU, immediate speech after Start/Continue, Stop/Paste/Cancel, repeated continuation and recovery, replacements, every custom action type, Enter and a modified key action, start/stop event shortcuts, Recent Copy/Delete, all four pill buttons, tray/clean exit, update progress, and a clean installer upgrade.
