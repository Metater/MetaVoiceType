# MetaVoiceType 1.4.0 release report

## Completed scope

- Added a true held recording keybind. The configured gesture is pressed when recording starts, released when it ends, and released defensively during shutdown.
- Added capture-tail draining. Frames already queued by the microphone at stop time are included before VAD flush, preventing final-word truncation without accepting later audio.
- Bounded deferred and active paste requests so the UI cannot remain in `Preparing` indefinitely.
- Added keyboard playback actions for custom voice commands, including single keys such as Enter.
- Updated the header subtitle/version treatment, listener/status alignment, command examples, and full-command-list pointer.
- Replaced the generic GPU eye icon with the official horizontal NVIDIA Newsroom logo asset at a larger size.
- Added explicit Version and Updates panels plus a dedicated Credits tab covering the runtime GitHub libraries.
- Changed CI packaging to download the prior release as a Velopack delta base, generate a best-size Zstandard delta, and remove the old base before upload.

## Verification

- `dotnet build MetaVoiceType.slnx -c Release --no-restore`: passed with 0 warnings and 0 errors.
- `dotnet test MetaVoiceType.slnx -c Release --no-restore`: passed, 92 tests, 0 failed, 0 skipped.
- `scripts/Test-ReleaseDecision.ps1`: passed.
- `git diff --check`: passed.
- `scripts/package.ps1 -Version 1.4.0 -SkipTests`: passed.
- Velopack verified `VelopackApp.Run()` and created the portable bundle, full package, setup executable, and `1.3.1 -> 1.4.0` delta.
- Delta result: 385 files processed, 5 patched, 380 unchanged; 442,239-byte delta versus the 1,619,479,050-byte full package.

## Release artifacts

- `MetaVoiceType-win-Setup.exe`
- `MetaVoiceType-win-Portable.zip`
- `MetaVoiceType-1.4.0-full.nupkg`
- `MetaVoiceType-1.4.0-delta.nupkg`
- Velopack release metadata (`assets.win.json`, `releases.win.json`, and `RELEASES`)

The local package is unsigned, matching previous local verification runs. The GitHub workflow publishes all generated artifacts from the tagged `v1.4.0` commit.
