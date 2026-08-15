# Testing

Run the deterministic suite:

```powershell
dotnet test MetaVoiceType.slnx -c Release
```

The suite covers catalog schemas and exact language IDs, no provider state in model metadata, confidence-independent matching, alternative ordering, Unicode grammar fallback, command validation, PCM conversion, session overlap, accepted-tail cleanup, duplicate/cancel paste behavior, atomic settings/history, retention at 100, recovery flush ordering, CLI parsing, managed-only policy, SHA verification, required-file commit, zip traversal rejection, and headless Avalonia window loading/layout.

## Windows diagnostics

Run against installed local models and hardware:

```powershell
dotnet src/MetaVoiceType/bin/Release/net10.0-windows/MetaVoiceType.dll --self-test --diagnostics
```

Useful options:

- `--list-audio-devices`: enumerate active capture devices
- `--install-models [--command-language ID]`: install and initialize Nemotron plus a selected Vosk model
- `--audio-file PATH --dictation-language LOCALE`: transcribe supplied audio
- `--test-command --command-language ID`: also require a configured Vosk command from supplied audio
- `--stress-minutes N`: stream the real default microphone through production session/decode code for N minutes
- `--paste-text TEXT`: time and execute the production clipboard/Ctrl+V transaction into the focused application
- `--recovery-crash-seconds N`: create a real in-progress recovery recording and terminate abruptly after N seconds
- `--force-cpu`: explicit runtime preference (CPU is the only V1 provider)
- `--reset-onboarding`: show onboarding again without deleting other settings

V1 hardware verification opened and switched between both active microphones. Synthetic speech checks covered English, Spanish, Russian, and Ukrainian; English, Russian, and Ukrainian Vosk command emission was verified. The generated Velopack setup was installed to an isolated clean directory and the installed stub completed `--self-test` with exit code 0.

Manual release QA should additionally cover clipboard insertion into at least Notepad, a browser field, and an Office-style editor; Ctrl+Space; tray hide/restore; Escape during recording; startup registration; crash/recovery; and the six command/state combinations.
