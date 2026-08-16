# TODO

## UI / UX updates requested

- Add support for a held keybind during recording for external mute workflows (ex: hold Discord mute key while recording).
- In the top-left header area beneath the app title, update subtitle text to local voice transcription / local voice dictation and commands.
- Display the version number directly after `MetaVoiceType` in the header, and remove the "Version" label/prefix.
- Replace the current NVIDIA logo on the Parakeet/GPU status badge with the official NVIDIA logo from branding, and increase its size so it is clearly visible.
- In the recording section, show multiple command examples for each action (start, continue, stop, paste, copy, cancel, etc.) instead of only a single command.
- Add text under recording section: `See full command list in the settings`.
- Fix/pin paste behavior so it does not get stuck on the Preparing state and spinner indefinitely after paste.- Fix top-right status chip alignment so the green status icon and "active" text are vertically centered/aligned on the same baseline.
- Remove the bullet separator in the Vosk command listener status label (e.g., "U.S. English · Active") so it no longer appears visually AI-generated.
- Split the About & updates area into separate sections/panels with explicit Version section, then Updates section.
- Add a dedicated "Credits" tab in Settings listing all GitHub libraries used, with links and descriptions copied from their repositories/projects.
- Add a feature to allow custom commands to send playback-like key events (e.g., a command for "meta enter" that simulates pressing Enter), enabling voice-triggered keypress execution.
- Investigate/fix possible recording finalization cutoff where the finalization phase may truncate the last few words before completion.