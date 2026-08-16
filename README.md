# MetaVoiceType

MetaVoiceType is a local-first Windows dictation app. Vosk listens locally for configurable voice commands; NVIDIA Parakeet transcribes only during recording. Command language and dictation language are independent.

[Set Up Discord Push-to-Mute for MetaVoiceType Using AutoHotkey](docs/Set Up Discord Push-to-Mute for MetaVoiceType Using AutoHotkey.md): Step-by-step guide to configure a reliable push-to-mute workflow while recording.

## Windows V1.4

- Windows 10/11 x64, .NET 10, and Avalonia
- Settings save automatically. Recording-event fields accept typed keys and mouse buttons such as `F24`, `Mouse4`, or `Ctrl+Mouse5`.
- Optional paste-on-shortcut-stop turns the recording shortcut into a one-key record-and-paste workflow.
- Each settings page can be reset independently; Version &amp; updates contains a global settings reset, and installed models can be deleted individually.
- User preferences are stored separately from the installation so they survive uninstall/reinstall unless explicitly reset.
- Self-contained installer; no separate .NET or CUDA Toolkit setup
- Automatic multilingual dictation with Parakeet TDT 0.6B v3, or English with v2
- NVIDIA CUDA preferred through the verified Sherpa runtime; automatic CPU fallback
- System theme by default, with Dark and Light choices
- Multiple spoken aliases for all seven built-ins and custom commands; Paste Recording is primary and Paste Here remains an English alias
- Grouped, literal boundary-aware word replacements before history, copy, and paste
- UTC transcript storage with DST-aware Windows local-time display
- Shared FftSharp speech spectrum in the live card and transparent floating pill
- Explicit paste lifecycle and paste-only pill state, including paste/new-recording overlap
- Purple Fluent interaction palette, responsive narrow layout, and internally scrolling live transcript
- Voice-triggered Program, PowerShell, Command Prompt, and keyboard actions—including single keys such as Enter
- Tap shortcuts at recording start/stop or hold a keybind for the full recording, suitable for Discord, OBS, and other apps
- Timestamp-aware pre-roll, capture-tail draining, and lossless managed capture queues preserve final words
- Velopack Zstandard delta updates download only changed package data when that is smaller than a full update

Run `artifacts/releases/MetaVoiceType-win-Setup.exe`, then complete onboarding. English (US) is the default Vosk command language; Automatic Parakeet V3 is the default dictation mode. Twelve downloadable command languages are available.

Closing the window keeps the listener in the tray. Use **Exit MetaVoiceType** from its tray menu to stop it.

## Discord mute while recording

1. Open Discord **Settings → Keybinds**.
2. Create a **Push to Mute** keybind.
3. In MetaVoiceType **Settings → General**, record it under **Hold while recording**.

MetaVoiceType holds the configured keybind down from recording start until recording ends. It does not read Discord's mute state. Boundary shortcut taps remain available for toggle-style workflows.

## Privacy, recovery, and builds

Audio, transcripts, settings, and history stay on this PC. Recovery PCM is removed after its corrected transcript is committed. Existing V1.1/V1.2 data and downloaded models remain in `%LOCALAPPDATA%\MetaVoiceType` during upgrade.

```powershell
dotnet restore MetaVoiceType.slnx
dotnet test MetaVoiceType.slnx -c Release
dotnet tool install --global vpk --version 1.2.0
./scripts/package.ps1
```

`Directory.Build.props` is the version source. Main-branch releases run tests and publish only when the matching `vX.Y.Z` release/tag does not already exist.

See [Architecture](docs/ARCHITECTURE.md), [Models](docs/MODELS.md), [Privacy](docs/PRIVACY.md), and [Testing](docs/TESTING.md).
