# MetaVoiceType

Open-source local voice-dictation application for Windows. Modern C#, Avalonia UI, NVIDIA-accelerated streaming ASR via sherpa-onnx + NVIDIA Nemotron 3.5 ASR, Vosk command recognition.

**Current status: Phase 0 (research) + Phase 1 (console prototype) complete.** See [REPORT-PHASE1.md](REPORT-PHASE1.md) for findings.

## Verified stack (Phase 0/1)

| Component | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.400-preview (dev only) | Prototype targets `net10.0` |
| sherpa-onnx (NuGet `org.k2fsa.sherpa.onnx`) | 1.13.5 | Apache-2.0; C# bindings + CPU runtime |
| sherpa-onnx CUDA 12.x win-x64 runtime | v1.13.5 (GitHub release asset) | `sherpa-onnx-c-api.dll` + `onnxruntime_providers_cuda.dll` |
| NVIDIA Nemotron 3.5 ASR Streaming 0.6B | 560ms int8 export (2026-06-11) | OpenMDW-1.1 (model), sherpa export on GitHub |
| CUDA 12.9 redistributables | cudart, cublas, cufft, curand, cusparse, nvjitlink | NVIDIA CUDA Toolkit license |
| cuDNN 9.25 | CUDA 12 variant | NVIDIA cuDNN license |
| PortAudioSharp2 | 1.0.6 | MIT; microphone capture |
| Vosk | 0.3.38 | Apache-2.0; Phase 4 |

## Model

`models/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11/` (encoder 628MB, decoder 14MB, joiner 9MB, tokens). Multilingual — 40 locales, auto language detection (`--language=auto`), native punctuation/capitalization, cache-aware streaming (no backlog over long sessions).

## NVIDIA runtime for GPU inference

The NuGet `org.k2fsa.sherpa.onnx` ships CPU-only `onnxruntime.dll`. GPU requires the sherpa CUDA release tarball plus NVIDIA redistributable DLLs:

```powershell
cd MetaVoiceType.ConsolePrototype
.\deploy-cuda.ps1          # copies CUDA DLLs into bin\Release\net10.0
```

`deploy-cuda.ps1` expects `..\cuda\sherpa-onnx-v1.13.5-cuda-12.x-cudnn-9.x-onnxruntime1.27.1-win-x64-cuda\{bin,lib}` to exist (extracted from the sherpa-onnx v1.13.5 release asset `sherpa-onnx-v1.13.5-cuda-12.x-cudnn-9.x-onnxruntime1.27.1-win-x64-cuda.tar.bz2` with NVIDIA redist DLLs placed in `bin/`).

## Running the prototype

CPU:
```powershell
dotnet run -c Release --no-build -- `
  --provider=cpu --num-threads=4 `
  --encoder=..\models\sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11\encoder.int8.onnx `
  --decoder=..\models\sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11\decoder.int8.onnx `
  --joiner=..\models\sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11\joiner.int8.onnx `
  --tokens=..\models\sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11\tokens.txt `
  --language=auto `
  --wav=..\models\sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11\test_wavs\es.wav
```

GPU: add `--provider=cuda` (with CUDA DLLs deployed).

Microphone: omit `--wav` (press Enter to stop and finalize). `--list-devices` shows input devices.

## Phase plan

- Phase 0 — research ✓
- Phase 1 — Nemotron console prototype ✓
- Phase 2 — multi-session streaming architecture
- Phase 3 — durable temporary recovery
- Phase 4 — Vosk commands
- Phase 5 — paste and clipboard system
- Phase 6 — keyboard shortcuts
- Phase 7 — Avalonia application
- Phase 8 — floating pill
- Phase 9 — tray/background operation
- Phase 10 — first-run setup
- Phase 11 — installer and releases
- Phase 12 — update system
- Phase 13 — reliability testing
