# Architecture

MetaVoiceType is a .NET 10 Avalonia desktop app using dependency injection and CommunityToolkit.Mvvm.

## Audio and recognition flow

1. `WindowsAudioCaptureService` captures 16 kHz mono PCM16 through WASAPI and moves callback data immediately to a managed channel.
2. Every frame reaches the active Vosk command recognizer. A dictation session receives frames only after Start Recording.
3. Vosk word timestamps are mapped to the shared global sample clock. Accepted control-command spans are removed from Parakeet audio before decode; a conservative text-tail fallback is used only when timestamps are absent. Confidence is passed through but never used.
4. Sherpa's Silero VAD emits bounded speech segments. `DecodeCoordinator` serializes offline Parakeet work away from the audio callback and preserves segment order.
5. A stopped session can finalize while a new session records. Stop→Paste binds to the stopped session and executes exactly once after its history commit.
6. `RecoveryWriter` flushes PCM before `JsonHistoryStore` commits. PCM is deleted only after the transcript is durable.

Vosk command language is separate from Parakeet dictation mode. Vosk grammar rebuilds atomically when built-in or custom phrases change. Parakeet backend switching retires an old recognizer only after every session using it completes.

## Provider bootstrap

The strongly typed catalog describes artifacts, never transient provider state. At runtime the app detects NVIDIA with `nvidia-smi`, validates the downloaded official Sherpa CUDA bundle, validates bundled NuGet CUDA/cuDNN dependencies, and installs a managed DLL resolver before the first Sherpa native call. CUDA initialization is warmed up; any failure recreates the same model with the CPU provider and surfaces the reason.

## Storage

`%LOCALAPPDATA%\MetaVoiceType` contains schema-versioned settings, atomic history, `Models\Vosk`, `Models\Parakeet`, `Models\Runtime`, temporary `Recovery`, and rolling `Logs`. Logs exclude audio and transcript bodies.

## Model transaction

`catalog → temporary .part → SHA-256 → safe extraction → required-file validation → atomic directory commit`

ZIP and tar.bz2 entries are constrained beneath the temporary root. Direct-file artifacts use the same validation and commit path.
