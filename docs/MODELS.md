# Models

## Parakeet dictation

`model-catalog.json` schema 2 contains generic dictation, VAD, and runtime artifacts. It is validated with `System.Text.Json` at startup and contains no acceleration/provider state.

| ID | Capability | Bytes | SHA-256 |
|---|---|---:|---|
| `parakeet-v2` | English, INT8 | 482,468,385 | `157c157bc51155e03e37d2466522a3a737dd9c72bb25f36eb18912964161e1ad` |
| `parakeet-v3` | 25-language automatic detection, INT8 | 487,170,055 | `5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf` |
| `silero-vad` | Local speech segmentation | 643,854 | `9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6` |
| `sherpa-cuda-12` | Sherpa 1.13.5 CUDA 12/cuDNN 9 runtime | 375,615,135 | `2d35c894f1ec4a3b6bed9aaa2b5895394d6afa85c5245a3fd071c8f3d3cae893` |

Both Parakeet entries require `encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx`, and `tokens.txt`. V2 and v3 are CC-BY-4.0. V3 languages are Bulgarian, Croatian, Czech, Danish, Dutch, English, Estonian, Finnish, French, German, Greek, Hungarian, Italian, Latvian, Lithuanian, Maltese, Polish, Portuguese, Romanian, Slovak, Slovenian, Spanish, Swedish, Russian, and Ukrainian.

## Vosk commands

`voice-command-languages.json` is a separate schema containing command-model identity, URLs, sizes, licenses, grammar behavior, and six default phrases. It exposes exactly twelve V1 languages and defaults to English (US). ASCII phrase sets use restricted grammar. Non-ASCII sets use normal decoding followed by exact configured-phrase matching because the official Windows binding cannot safely marshal Unicode runtime grammar. Confidence never influences acceptance, ordering, logging, or UI.
