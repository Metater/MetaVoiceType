# Models

## Nemotron dictation

The bundled `model-catalog.json` identifies NVIDIA Nemotron 3.5 ASR Streaming 0.6B, the official sherpa-onnx int8 archive, expected extraction directory, four required files, published SHA-256, byte estimate, OpenMDW-1.1 license URL, and documented language tiers. It deliberately contains no CPU/GPU state.

The download is written to `.part`, checked against SHA-256 `c6bf5e0df765f9d5b43bc9e0536d4b4b3e7d40bdf5ecf13e45f134c51c05ae3a`, safely extracted, validated, and committed. Nemotron defaults to `auto`; forced dictation values use the official locale identifiers such as `es-ES`, `ru-RU`, and `uk-UA`.

The V1 runtime uses sherpa-onnx's supported Windows CPU NuGet runtime. A clean supported Windows CUDA NuGet route was not available, so no handwritten CUDA/native integration was added.

## Vosk commands

`voice-command-languages.json` contains exactly 30 selectable language models, archive URLs, verified current archive sizes, model names, licenses, six defaults, and grammar mode. English (US) is the default. All 30 URLs returned HTTP 200 during V1 verification.

The official Vosk C# 0.3.38 binding marshals runtime grammar through the Windows ANSI string path. Consequently, ASCII command sets use restricted grammar; non-ASCII sets use the publisher model's normal decoder and then exact configured-phrase matching. This avoids custom native bindings and works independently of the Windows system locale. The Ukrainian full `vosk-model-uk-v3` uses that documented fallback because it has a precompiled HCLG graph and its phrases are non-ASCII.

Vosk confidence values are intentionally ignored. Alternatives are considered in source order; no threshold, ranking, display, or logging decision uses confidence.
