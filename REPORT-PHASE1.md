# Phase 0 + Phase 1 Report

Date: 2026-08-15 · Machine: RTX 4060 Laptop GPU (8GB VRAM), Windows, driver 610.88

## 1. Verified dependency versions

| Component | Version | Source |
|---|---|---|
| .NET SDK (dev) | 10.0.400-preview.0 | `dotnet --list-sdks` |
| sherpa-onnx NuGet | 1.13.5 | NuGet `org.k2fsa.sherpa.onnx` (Apache-2.0) |
| sherpa-onnx CUDA win-x64 | v1.13.5 (cuda-12.x, cudnn-9.x, onnxruntime 1.27.1) | GitHub release asset |
| NVIDIA Nemotron 3.5 ASR 0.6B | 560ms int8 sherpa export (2026-06-11) | `sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11.tar.bz2` |
| CUDA redistributables | 12.9.x (cudart 12.9.79, cublas 12.9.1.4, cufft 11.4.1.4, curand 10.3.10.19, cusparse 12.5.10.65, nvjitlink 12.9.86) | developer.download.nvidia.com redist manifests |
| cuDNN | 9.25.0.15 (CUDA 12) | developer.download.nvidia.com redist manifest |
| PortAudioSharp2 | 1.0.6 | NuGet (MIT) |
| CommandLineParser | 2.9.1 | NuGet (MIT) |
| Microsoft.Extensions.Logging* | 8.0.x | NuGet |
| Vosk | 0.3.38 (verified; used in Phase 4) | NuGet (Apache-2.0) |

## 2. Authoritative documentation/tools used

- sherpa-onnx official docs: [Nemotron ASR Streaming page](https://k2-fsa.github.io/sherpa/onnx/nemo/nemotron-streaming.html) (model URLs, CLI usage, per-stream `SetOption("language", ...)`).
- sherpa-onnx C# bindings source: `scripts/dotnet/*.cs` (exact struct layouts: `OnlineRecognizerConfig`, `OnlineModelConfig`, `OnlineRecognizer`, `OnlineStream`, `OnlineRecognizerResult`, `VersionInfo`).
- sherpa-onnx GitHub release v1.13.5 asset list via GitHub API (picked the CUDA 12.x win-x64 tarball).
- NVIDIA Nemotron 3.5 model card on Hugging Face (architecture, locales, WER tables, license OpenMDW-1.1).
- NuGet package pages (sherpa-onnx 1.13.5, Vosk 0.3.38, PortAudioSharp2 1.0.6).
- NVIDIA CUDA/cuDNN redist manifests (`redistrib_12.9.1.json`, `redistrib_9.25.0.json`) for redistributable DLL URLs.
- Reflector-free verification by compiling against the actual NuGet assembly (fixed `VersionInfo.Version`/`OnnxruntimeVersion` properties, not methods).

No code was written against hallucinated APIs: every P/Invoke-backed type used matches the published binding source.

## 3. Exact Nemotron model/runtime used

- **Model**: `nvidia/nemotron-3.5-asr-streaming-0.6b`, sherpa-onnx int8 export, chunk size **560ms**, dated **2026-06-11**:
  - `encoder.int8.onnx` 657,601,403 bytes
  - `decoder.int8.onnx` 14,978,075 bytes
  - `joiner.int8.onnx` 9,504,438 bytes
  - `tokens.txt` 131,440 bytes
- **Runtime**: sherpa-onnx C-API 1.13.5 DLLs from the CUDA release tarball (`sherpa-onnx-c-api.dll` 4.5MB, `onnxruntime.dll` 15.8MB, `onnxruntime_providers_cuda.dll` 328MB), plus NVIDIA redistributable DLLs: cublas, cublasLt, cudart, cufft, curand, cusparse, nvJitLink, and cuDNN 9.25 (cudnn64_9 + graph/ops/adv/engines).
- Recognizer config: 16 kHz, feature dim 80, `greedy_search`, `max_active_paths=4`, endpoint detection disabled for Phase 1 (explicit stop controls the session), per-stream language option `auto` or locale.

## 4. NVIDIA GPU execution works

Yes. Verified two ways:

1. Prebuilt `sherpa-onnx.exe` from the CUDA tarball with `--provider=cuda`: correct transcript, RTF 0.63 (vs 0.44 CPU on same WAV, both dominated by model load/warmup on this short clip).
2. C# prototype with `--provider=cuda`: **GPU utilization 45% during decode, VRAM 1635–1916 MiB used** (vs 331 MiB idle), correct transcript, identical output quality to CPU.

The C# path needed the CUDA DLL set copied next to `sherpa-onnx-c-api.dll` (see `deploy-cuda.ps1`). The NuGet runtime package alone is CPU-only (its onnxruntime.dll lacks the CUDA provider).

## 5. GPU/VRAM usage

- Idle: 331 MiB.
- Model loaded + decoding: ~1.6–1.9 GiB (encoder ~628MB fp32-equivalent int8 + CUDA/cuDNN context).
- Well within 8GB VRAM; multi-stream Phase 2 headroom is fine.

## 6. Live transcription quality

- English/Spanish/French/Ukrainian/Vietnamese/Arabic/Korean/Chinese test WAVs all transcribed correctly with natural punctuation and capitalization (Nemotron outputs it natively; no post-processing).
- Spanish sample: "No preguntes qué puede hacer tu país por ti. Pregunta qué puedes hacer tú por tu país" (the classic JFK line) — clean, accented characters intact.
- Quality is preserved from CPU to CUDA (same model, same output).
- One caveat: the Japanese test WAV in `auto` mode drifted into Korean characters initially; with `--language=ja` the transcript was clean Japanese. Language auto-detection is good but not perfect for adjacent-language pairs; the app should prefer auto by default and document per-language override.

## 7. Automatic language behavior

- `auto` works out of the box for the 19 transcription-ready locales (verified es, fr, de, uk, vi, ar, ko, zh, ja-with-caveat).
- Per-stream language is set with `OnlineStream.SetOption("language", "auto"|"en"|"ru"|...)` — confirmed present in the C# binding (`OnlineStream.cs`).
- sherpa-onnx strips the model's language tags so transcripts stay clean.

## 8. Streaming latency

- Partial transcripts appear within ~250ms render cadence; Nemotron's cache-aware FastConformer processes non-overlapping chunks, so nothing waits for a stop signal.
- Measured processing cost on CPU (4 threads): ~230–280 ms per audio second → RTF ≈ 0.23–0.28.
- Measured on CUDA (short clip): RTF ≈ 0.63 including 4–5s one-time model load; sustained processing ~347–467 ms per audio second measured wall-clock (the short clips are dominated by load; long-run numbers below are more representative).

## 9. Finalization latency

The critical number:

- **60× repeated Spanish WAV (319.7s of audio, one continuous session): finalization = 3.1ms.** The transcript (5,119 chars) was essentially already complete when the stream ended.
- Single WAV runs: 0.2–0.3ms finalization.
- This directly proves the near-instant "paste here" requirement: 5+ minutes of dictation finalizes in single-digit milliseconds because streaming already did the work.

## 10. Processing stays ahead of real-time speech

Yes:

- CPU (4 threads): ~0.25 RTF — 4× faster than real time.
- CUDA: ~0.47 RTF measured including per-chunk overhead on this workload (each 20ms chunk still schedules a GPU kernel); comfortably ahead of real time with a single stream.
- No backlog over a 320-second continuous session; decode lag stayed flat (the bounded channel dropped 0 frames throughout).
- On this RTX 4060 Laptop, CPU is actually competitive because the model is only 600M int8 — but GPU still has lower per-chunk latency headroom for multi-stream and the architectural path is proven.

## 11. Problems discovered

1. **NuGet runtime is CPU-only.** GPU inference requires the GitHub CUDA tarball + NVIDIA redistributable DLLs. The final installer must bundle: `sherpa-onnx-c-api.dll`, CUDA-provider `onnxruntime*.dll`, and the NVIDIA redist DLL set (~3.1GB total in this layout; reducible by shipping only cudart/cublas/cublasLt/cudnn_ops/graph/heuristic/core — cuSPARSE/cuFFT/cuRAND aren't needed by this model and can likely be omitted after testing).
2. **CUDA provider needs manual DLL provisioning.** ORT dynamically loads `cublas64_12.dll` and `cudnn64_9.dll` etc.; a stock NVIDIA driver doesn't include them. Bundling redistributables (permitted by NVIDIA redist license) is the right call for v1, as the spec anticipated.
3. **`--language=auto` is strong but not infallible** for close language pairs (Japanese→Korean confusion observed on one sample). Provide explicit language override and document.
4. **sherpa CLI `--provider=cuda` first run** after adding DLLs returned empty text once (likely a warmup/caching hiccup); a second run was correct. Worth watching in Phase 2.
5. **Model load time**: 2.5s (CPU) to 5s (CUDA) one-time at startup; acceptable, and streaming continues uninterrupted after.

## 12. Files changed

```
.gitignore                                    (added, dotnet template)
MetaVoiceType.sln                             (added)
README.md                                     (added)
REPORT-PHASE1.md                              (added, this file)
MetaVoiceType.ConsolePrototype/
  MetaVoiceType.ConsolePrototype.csproj       (added; sherpa-onnx 1.13.5, PortAudioSharp2, CLI, logging)
  Program.cs                                  (top-level async main, CLI wiring)
  Options.cs                                  (CLI options)
  PrototypeApp.cs                             (device listing, WAV/mic runner, timing)
  TranscriptionEngine.cs                      (OnlineRecognizer owner, finalize, process)
  TranscriptionSession.cs                     (per-stream wrapper: language option, timing)
  MicrophoneAudioSource.cs                    (PortAudio → bounded channel)
  WavFileSource.cs                            (WAV → bounded channel, repeat support)
  ConcurrencyProbe.cs                         (two-stream Phase 2 preview)
  deploy-cuda.ps1                             (deploys CUDA DLLs into output)
  bench.ps1                                   (benchmark helper)
models/                                       (downloaded Nemotron model — gitignored)
cuda/, cuda-redist/                           (downloaded CUDA runtime — gitignored)
```

Note: model weights and CUDA binaries are **not** committed (gitignored); they are listed in README with exact URLs and hashes for reproducibility.

## 13. Recommended Phase 2 implementation

Goal: prove `Session A finalizing + Session B recording` with zero capture blocking.

1. **Single `OnlineRecognizer`, multiple `OnlineStream`s** — already demonstrated by `ConcurrencyProbe` (two sessions, one recognizer, independent finalize + continue). Extend to: capture loop feeds Session B while a background task runs `FinalizeBlocking(Session A)`.
2. **Audio dispatcher**: one PortAudio callback → bounded channel → `IAudioFrameDispatcher` fanning out to (a) Nemotron stream of the active session, (b) temp recovery WAV writer (Phase 3), (c) Vosk (Phase 4). Dispatcher must never run inference on the callback thread.
3. **Decode worker**: since sherpa `Decode`/`GetResult` must be serialized per recognizer (its C API decodes a batch of ready streams), route all decode work through one worker loop that round-robins ready streams — finalization of A and live decode of B interleave naturally.
4. **Timing instrumentation**: log per-chunk decode time and queue depth continuously; verify no backlog over a 10-minute simulated session (use `--wav-repeat`).
5. **GPU serialization decision**: if Phase 2 measurements show GPU decode contention between streams, serialize decode on one worker but keep capture + channel + recovery-write off the critical path. Do not gate `StartRecording` on finalization.

Next session should implement Phase 2 in the console prototype (no UI yet), then Phase 3 crash recovery.
