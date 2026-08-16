# Models and deterministic artifacts

MetaVoiceType keeps dictation/runtime and Vosk command catalogs separate. Every remote entry has a versioned identity, exact URL, byte count, SHA-256, archive type, expected directory, required files, capability, and license. CPU/GPU selection is runtime state and is not catalog metadata.

## Sherpa / Parakeet artifacts

All four assets are from `k2-fsa/sherpa-onnx` and were verified through the official GitHub release API.

| ID | Release | Asset ID | Bytes | SHA-256 |
|---|---|---:|---:|---|
| `parakeet-v2` | `asr-models` | 283097678 | 482,468,385 | `157c157bc51155e03e37d2466522a3a737dd9c72bb25f36eb18912964161e1ad` |
| `parakeet-v3` | `asr-models` | 283097583 | 487,170,055 | `5793d0fd397c5778d2cf2126994d58e9d56b1be7c04d13c7a15bb1b4eafb16bf` |
| `silero-vad` | `asr-models` | 271935959 | 643,854 | `9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6` |
| `sherpa-cuda-12` | `v1.13.5` | 509879675 | 375,615,135 | `2d35c894f1ec4a3b6bed9aaa2b5895394d6afa85c5245a3fd071c8f3d3cae893` |

Parakeet artifacts require `encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx`, and `tokens.txt`. V2 and V3 are CC-BY-4.0. V2 is English. V3 automatically detects Bulgarian, Croatian, Czech, Danish, Dutch, English, Estonian, Finnish, French, German, Greek, Hungarian, Italian, Latvian, Lithuanian, Maltese, Polish, Portuguese, Romanian, Slovak, Slovenian, Spanish, Swedish, Russian, and Ukrainian. The current Sherpa offline transducer wrapper does not provide a useful forced-language hint, so MetaVoiceType does not expose one.

## Vosk command artifacts

These exact versioned archives are published at [Alpha Cephei's model catalog](https://alphacephei.com/vosk/models). That host does not expose GitHub release asset IDs, so `assetId` is explicitly null and deterministic identity is the exact model/version filename plus bytes and the SHA-256 computed from the official archive.

| Language | Asset | Bytes | SHA-256 |
|---|---|---:|---|
| English (US) | `vosk-model-small-en-us-0.15.zip` | 41,205,931 | `30f26242c4eb449f948e42cb302dd7a686cb29a3423a8367f99ff41780942498` |
| Russian | `vosk-model-small-ru-0.22.zip` | 46,236,750 | `961d5ff98a17f4aa6de69864d0aa71fa5bac682301d2b5d17a3f24c5c99a46d4` |
| French | `vosk-model-small-fr-0.22.zip` | 42,233,323 | `cabf6180e177eb9b3a9a9d43a437bd5e549f3a7d09525e5d69a3fed787be12ad` |
| German | `vosk-model-small-de-0.15.zip` | 46,499,967 | `b7e53c90b1f0a38456f4cd62b366ecd58803cd97cd42b06438e2c131713d5e43` |
| Spanish | `vosk-model-small-es-0.42.zip` | 39,817,833 | `09b239888f633ef2f0b4e09736e3d9936acfd810bc65d53fad45261762c6511f` |
| Portuguese (Brazil) | `vosk-model-small-pt-0.3.zip` | 32,453,112 | `6e1ce909032e1afa7a88e68a3d628ecafff302bdf195befab308826c395e93b7` |
| Italian | `vosk-model-small-it-0.22.zip` | 49,665,141 | `9ec65e75861d1c6c2e457cccd932705340dcdf233f5b239f00733b4de0bf3267` |
| Dutch | `vosk-model-small-nl-0.22.zip` | 40,441,176 | `039811c3b829de64e4f123a9f684a53784005b212a346ac0b899dc7efce2ed0a` |
| Ukrainian | `vosk-model-uk-v3.zip` | 371,048,965 | `b1a0dbb9a19bfb6cdbad5bb4d43b71281ab57ad4a8c2a372656c83b994b72b1f` |
| Swedish | `vosk-model-small-sv-rhasspy-0.15.zip` | 303,504,931 | `5b54f2ca8cacf9766588d05f6c8ca7e8921f054f050726e29d3516e78aefe054` |
| Czech | `vosk-model-small-cs-0.4-rhasspy.zip` | 46,088,666 | `287c3bbefc8ad67b4ab9636eecef3d62acc3719990777d03e226db5a7f19fbda` |
| Polish | `vosk-model-small-pl-0.22.zip` | 52,979,372 | `c4cd16498ea544f446f9e9a55cbd602b71cfe5a2b6f2b0834d81e1b6fce15f0d` |

Each requires `am/final.mdl` and `conf/mfcc.conf`. Command models govern only Vosk phrases; they never constrain Parakeet dictation language. Vosk confidence is passed through for diagnostics compatibility but is never used for acceptance or ordering.

## Install transaction

`catalog → .part → expected bytes → SHA-256 → safe extraction → required files → atomic directory commit → recognizer initialization → Ready`

The active Vosk listener remains unchanged until the replacement archive is fully verified, extracted, loaded, its grammar built, and the recognizer initialized. Hash/size failures delete the temporary archive and surface a retryable error.
