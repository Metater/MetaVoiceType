# Dependencies

All versions are centrally pinned in `Directory.Packages.props`.

| Package | Version | Purpose |
|---|---:|---|
| Avalonia.Desktop / Themes.Fluent / Fonts.Inter | 12.1.1 | Windows UI, theme, font, tray |
| Material.Icons.Avalonia | 3.0.2 | UI iconography |
| CommunityToolkit.Mvvm | 8.4.2 | Observable state and commands |
| Microsoft.Extensions.Hosting | 10.0.10 | Hosting and dependency injection |
| System.CommandLine | 2.0.10 | Developer diagnostics |
| NAudio | 2.3.0 | WASAPI capture, cues, diagnostic resampling |
| Vosk | 0.3.38 | Local voice-command recognition |
| org.k2fsa.sherpa.onnx | 1.13.4 | Managed Nemotron streaming wrapper and CPU runtime |
| SharpHook | 7.1.3 | Global hotkey and paste keystroke |
| TextCopy | 6.2.1 | Clipboard access |
| Serilog.Extensions.Hosting | 10.0.0 | Structured host logging |
| Serilog.Sinks.File | 7.0.0 | Rolling local logs |
| SharpCompress | 0.50.1 | Safe tar.bz2 extraction |
| Velopack | 1.2.0 | Installer and updates |
| xunit.v3 | 3.2.2 | Tests |
| Microsoft.NET.Test.Sdk | 18.8.1 | Test host |
| Avalonia.Headless.XUnit | 12.1.1 | Avalonia test support |

There is no application-authored C, C++, CMake, or native binding code. Native binaries arrive only through the established NuGet packages above. The repository test suite enforces this policy.
