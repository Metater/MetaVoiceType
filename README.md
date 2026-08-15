# MetaVoiceType

MetaVoiceType is a local-first Windows dictation app. Say a configurable command to start recording, dictate naturally, then say another command to copy or paste the finished transcript. Dictation and command recognition run on your PC; microphone audio and transcript text are not sent to a MetaVoiceType service.

## Requirements

- Windows 10 or Windows 11, x64
- A working Windows capture device
- About 1 GB free for the app, the default command model, and Nemotron
- Internet access only when downloading models or checking for app updates

The V1 installer is self-contained and does not require a separately installed .NET runtime. Nemotron runs through the supported sherpa-onnx CPU NuGet runtime. NVIDIA CUDA was evaluated, but no clean supported Windows CUDA NuGet path was available for this model/runtime combination; MetaVoiceType therefore falls back to CPU and reports the active mode in Settings.

## Install and get started

1. Run `MetaVoiceType-Setup.exe` from the release artifacts.
2. Choose a voice-command language and download its Vosk model.
3. Download the verified Nemotron model (about 475 MB).
4. Choose a microphone and whether MetaVoiceType should start with Windows.
5. Finish onboarding, then say **start recording** or press **Ctrl+Space**.

The default English commands are:

- Start Recording
- Stop Recording
- Paste Here
- Cancel Recording
- Cancel Paste
- Copy Recording to Clipboard

All six phrases are editable per voice-command language. Reset affects only the currently selected language. Vosk voice-command language and Nemotron dictation language are independent: changing one never restricts the other.

Closing the window leaves command listening active in the system tray. Use **Exit MetaVoiceType** from the tray menu to stop the app.

## Privacy and recovery

MetaVoiceType uses one microphone capture pipeline. Vosk receives the live stream while the app is running; Nemotron receives audio only during an active dictation. During dictation, raw PCM is written to a recovery area. The PCM is deleted only after the transcript is atomically stored in history. If the app or PC stops unexpectedly, MetaVoiceType detects and finalizes the interrupted recording on the next launch. History retains the newest 100 items.

See [Privacy](docs/PRIVACY.md) and [Models](docs/MODELS.md) for details.

## Build from source

Install the .NET 10 SDK on Windows, then run:

```powershell
dotnet restore MetaVoiceType.slnx
dotnet test MetaVoiceType.slnx -c Release
dotnet publish src/MetaVoiceType/MetaVoiceType.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish
```

To create the installer, install `vpk` 1.2.0 and run `scripts/package.ps1`. Diagnostic options include `--self-test`, `--list-audio-devices`, `--diagnostics`, `--force-cpu`, `--reset-onboarding`, `--install-models`, and the audio/stress options documented in [Testing](docs/TESTING.md).

Architecture, dependency, test, and release details are in the [`docs`](docs) folder and [REPORT-V1.md](REPORT-V1.md).
