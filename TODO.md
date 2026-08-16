# TODO

## UI / UX updates requested

- [x] Add support for a held keybind during recording for external mute workflows (ex: hold Discord mute key while recording).
- [x] In the top-left header area beneath the app title, update subtitle text to local voice transcription / local voice dictation and commands.
- [x] Display the version number directly after `MetaVoiceType` in the header, and remove the "Version" label/prefix.
- [x] Replace the current NVIDIA logo on the Parakeet/GPU status badge with the official NVIDIA logo from branding, and increase its size so it is clearly visible.
- [x] In the recording section, show command examples for start, continue, stop, paste, copy, and cancel actions instead of only a single command.
- [x] Add text under recording section: `See full command list in the settings`.
- [x] Fix/pin paste behavior so it cannot remain in the Preparing state and spinner indefinitely after paste.
- [x] Fix top-right status chip alignment so the green status icon and "active" text are vertically centered/aligned on the same baseline.
- [x] Remove the bullet separator in the Vosk command listener status label (e.g., "U.S. English · Active").
- [x] Split the About & updates area into separate panels with an explicit Version section followed by Updates.
- [x] Add a dedicated Credits tab in Settings listing all GitHub libraries used, with links and project descriptions.
- [x] Add custom-command playback key events, including single keys such as Enter.
- [x] Fix recording finalization cutoff by draining capture frames queued before stop into the session before VAD flush.
- [x] Switch the release pipeline to Velopack Zstandard differential updates, with a full-package fallback.
- [x] Clean out the voice commands from what you say by partial match at the beginning and end.
- [x] Add a checkbox/toggle to make Ctrl+Space perform paste on complete.
- [x] Add support for high function keys (e.g., F24) in record hotkey configuration in settings, and support every key press and mouse button type cleanly.
- [x] Auto-save settings and remove redundant save buttons.
- [x] Keep user preferences outside the install-owned local application directory so they survive uninstall/reinstall.
- [x] Add per-page resets, a global settings reset, and per-model deletion controls.
- [x] Correct CPU-only mode transitions and use consistent success/warning/error status colors.
