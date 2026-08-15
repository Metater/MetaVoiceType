# Dependencies

Direct versions are centrally pinned in `Directory.Packages.props`.

| Package | Version | Purpose |
|---|---:|---|
| Avalonia desktop/theme/fonts | 12.1.1 | Windows UI, semantic themes, tray |
| Material.Icons.Avalonia | 3.0.2 | Icons |
| CommunityToolkit.Mvvm | 8.4.2 | Observable state and commands |
| Microsoft.Extensions.Hosting | 10.0.10 | Hosting and DI |
| System.CommandLine | 2.0.10 | Diagnostics |
| NAudio | 2.3.0 | WASAPI, cues, resampling |
| Vosk | 0.3.38 | Offline command recognition |
| org.k2fsa.sherpa.onnx | 1.13.5 | Managed Parakeet/VAD wrapper and CPU runtime |
| NtvLibs CUDA 12 cuBLAS | 12.8.1 | NuGet-delivered Windows CUDA dependencies |
| NtvLibs cuDNN CUDA 12 | 9.8.0.87 | NuGet-delivered Windows cuDNN dependencies |
| SharpHook | 7.1.3 | Global hotkey and managed keystrokes |
| TextCopy | 6.2.1 | Clipboard |
| Serilog hosting/file | 10.0.0 / 7.0.0 | Local diagnostics |
| SharpCompress | 0.50.1 | tar.bz2 extraction |
| Velopack | 1.2.0 | Setup and updates |
| xUnit / Test SDK / Avalonia Headless | 3.2.2 / 18.8.1 / 12.1.1 | Tests |

There is no application-authored C, C++, CMake, P/Invoke, or native build. Sherpa, Vosk, CUDA, and cuDNN binaries arrive through established packages or verified publisher artifacts. A repository test enforces this boundary.
