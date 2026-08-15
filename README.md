# MetaVoiceType

MetaVoiceType is a local-first Windows dictation app. Vosk listens locally for configurable voice commands; NVIDIA Parakeet transcribes only while a recording is active. Command language and dictation language are intentionally independent.

## Windows V1

- Windows 10/11 x64 and a working capture device
- Self-contained installer; no separate .NET or CUDA Toolkit installation
- Automatic dictation: multilingual Parakeet TDT 0.6B v3
- English dictation: Parakeet TDT 0.6B v2
- NVIDIA CUDA 12/cuDNN 9 preferred when a compatible GPU and the verified Sherpa runtime are available; automatic CPU fallback otherwise
- About 6 GB of free space recommended for setup, bundled GPU libraries, and both optional dictation models
- Network access only for model downloads and optional update checks

Run `artifacts/releases/MetaVoiceType-win-Setup.exe`, then complete the seven setup steps. English (US) is the default Vosk command language and Automatic is the default Parakeet mode. Continue is disabled until each selected model has actually initialized.

The six built-in English phrases are Start Recording, Stop Recording, Paste Here, Cancel Recording, Cancel Paste, and Copy Recording to Clipboard. Phrases are editable per Vosk language. V1 exposes twelve command languages: English (US), Russian, French, German, Spanish, Portuguese (Brazil), Italian, Dutch, Ukrainian, Swedish, Czech, and Polish.

Custom commands can launch a program, run PowerShell or Command Prompt text, or send a keyboard shortcut. Each belongs to one Vosk command language. The global recording shortcut defaults to `Ctrl+Space` and can be changed without restarting. Optional Discord auto-mute uses Discord's official local RPC and clearly reports when authorization is unavailable.

Closing the window keeps the local command listener in the tray. Use **Exit MetaVoiceType** from the tray menu to stop it.

## Privacy and recovery

No MetaVoiceType service receives microphone audio or transcripts. During a recording, PCM is retained only in the local recovery directory until history is committed atomically. Interrupted recordings are recovered on the next launch. History retains the newest 100 exact transcripts.

See [Privacy](docs/PRIVACY.md), [Models](docs/MODELS.md), and [Architecture](docs/ARCHITECTURE.md).

## Build and package

Install the .NET 10 SDK on Windows:

```powershell
dotnet restore MetaVoiceType.slnx
dotnet test MetaVoiceType.slnx -c Release
dotnet tool install --global vpk --version 1.2.0
./scripts/package.ps1
```

`Directory.Build.props` is the single version source. A push to `main` creates a GitHub release only when that version does not already have a `vX.Y.Z` tag. See [Testing](docs/TESTING.md) and [REPORT-V1-REVIEW.md](REPORT-V1-REVIEW.md).
