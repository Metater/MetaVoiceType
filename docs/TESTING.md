# Testing

```powershell
dotnet test MetaVoiceType.slnx -c Release
```

The 36 deterministic tests cover catalogs, exact language exposure, timestamp mapping, confidence independence, Unicode fallback, custom commands, hotkeys, Discord prior-state/overlap behavior, PCM, VAD segment ordering, command-audio exclusion, Stop→Paste during blocked finalization, new-session overlap, recovery ordering, exact paste/copy, atomic storage, safe downloads, managed-only policy, XAML construction, and a headless mandatory Continue click.

## Windows diagnostics

```powershell
dotnet run --project src/MetaVoiceType/MetaVoiceType.csproj -- --self-test
```

- `--install-models --dictation-language auto|en --command-language ID`
- `--audio-file PATH [--test-command]`
- `--force-cpu`
- `--list-audio-devices`
- `--stress-minutes N`
- `--paste-text TEXT`
- `--recovery-crash-seconds N`
- `--reset-onboarding`

V1 evidence includes two physical WASAPI devices; real v2/v3 CUDA initialization on an RTX 4060 Laptop GPU; exact GPU and forced-CPU transcription of a generated WAV; English Vosk command emission; safe hashes/extraction; Release tests; visual first-run/main/pill inspection; and final Velopack creation. Longer unattended stress, cross-application paste, tray/startup, and Discord-authorized-account checks remain useful owner smoke tests.
