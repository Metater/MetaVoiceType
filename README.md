# MetaVoiceType

MetaVoiceType is a local-first Windows dictation app. Vosk listens locally for configurable voice commands; NVIDIA Parakeet transcribes only during recording. Command language and dictation language are independent.

## Windows V1.2

- Windows 10/11 x64, .NET 10, and Avalonia
- Self-contained installer; no separate .NET or CUDA Toolkit setup
- Automatic multilingual dictation with Parakeet TDT 0.6B v3, or English with v2
- NVIDIA CUDA preferred through the verified Sherpa runtime; automatic CPU fallback
- System theme by default, with Dark and Light choices
- Seven editable built-in commands: Start, Continue, Stop, Paste Recording, Cancel Recording, Cancel Paste, and Copy
- Literal boundary-aware word replacements before history, copy, and paste
- Voice-triggered Program, PowerShell, Command Prompt, and keyboard actions—including single keys such as Enter
- Generic shortcuts when recording starts/stops, suitable for Discord, OBS, and other apps
- One-second timestamp-aware pre-roll and lossless managed capture queues

Run `artifacts/releases/MetaVoiceType-win-Setup.exe`, then complete onboarding. English (US) is the default Vosk command language; Automatic Parakeet V3 is the default dictation mode. Twelve downloadable command languages are available.

Closing the window keeps the listener in the tray. Use **Exit MetaVoiceType** from its tray menu to stop it.

## Discord mute while recording

1. Open Discord **Settings → Keybinds**.
2. Create a **Toggle Mute** keybind.
3. In MetaVoiceType **Settings → General**, record the same keybind for both **When recording starts** and **When recording stops**.

MetaVoiceType simply plays these shortcuts at recording start and end. It does not read Discord's mute state. The same feature works with any application shortcut.

## Privacy, recovery, and builds

Audio, transcripts, settings, and history stay on this PC. Recovery PCM is removed after its corrected transcript is committed. Existing V1.1 data and downloaded models remain in `%LOCALAPPDATA%\MetaVoiceType` during upgrade.

```powershell
dotnet restore MetaVoiceType.slnx
dotnet test MetaVoiceType.slnx -c Release
dotnet tool install --global vpk --version 1.2.0
./scripts/package.ps1
```

`Directory.Build.props` is the version source. Main-branch releases run tests and publish only when the matching `vX.Y.Z` release/tag does not already exist.

See [Architecture](docs/ARCHITECTURE.md), [Models](docs/MODELS.md), [Privacy](docs/PRIVACY.md), and [Testing](docs/TESTING.md).
