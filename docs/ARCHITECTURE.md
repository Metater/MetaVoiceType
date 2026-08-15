# Architecture

MetaVoiceType is a .NET 10 Avalonia desktop app hosted with `Microsoft.Extensions.Hosting`. Services are constructed once through dependency injection; UI state is exposed through CommunityToolkit.Mvvm observable models.

## Runtime flow

1. `WindowsAudioCaptureService` opens one WASAPI capture at 16 kHz mono PCM16. The hardware callback only copies data into a channel.
2. The audio dispatch worker sends every frame to Vosk. While a dictation session is active, it also sends the same frame to the session and recovery writer.
3. `DecodeCoordinator` owns serialized sherpa-onnx decode work. A stopped session finalizes in the background while live work from a newly started session keeps priority.
4. `RecoveryWriter` appends raw PCM and atomic metadata. Session commit waits for the recovery stream to flush and close, stores history atomically, then deletes PCM.
5. `PasteCoordinator` provides a single cancellable clipboard/paste transaction and rejects duplicates while one is pending.

Vosk command language is separate from Nemotron dictation language. Command models, default command phrases, and per-language overrides belong to the Vosk side. Nemotron defaults to automatic language detection and uses an independent IETF locale selection when forced.

## Storage

User data lives below `%LOCALAPPDATA%\MetaVoiceType`:

- `settings.json`: schema-versioned settings and per-language command overrides
- `history.json`: atomic newest-first transcript history (maximum 100)
- `Models\Vosk` and `Models\Nemotron`: committed model directories
- `Recovery`: temporary raw PCM plus session metadata
- `Logs`: rolling structured diagnostics; transcript bodies and microphone audio are never logged

## Model installation transaction

The strongly typed JSON catalog is validated at startup. Installation is generic:

`catalog → .part download → SHA-256 when published → safe extraction → required-file validation → atomic directory move`

Archive entries are resolved beneath a temporary root and path traversal is rejected. Runtime provider selection is not model metadata.

## Windows integration

SharpHook supplies the global Ctrl+Space listener and managed Ctrl+V simulation. TextCopy supplies clipboard access. Avalonia owns the main window, non-activating recording pill, and tray lifecycle. Velopack owns per-user setup and update application.
