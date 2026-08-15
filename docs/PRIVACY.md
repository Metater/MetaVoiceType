# Privacy

- Vosk processes microphone frames locally whenever its selected command model is active.
- Parakeet receives audio only during an active dictation.
- No MetaVoiceType service receives microphone audio, commands, transcripts, clipboard contents, or history.
- Recovery PCM exists locally only until its transcript is atomically stored; interrupted PCM is recovered on next launch.
- Logs contain lifecycle, provider, timing, queue, and error data, never transcript bodies or audio.
- Settings and the newest 100 exact transcripts remain below `%LOCALAPPDATA%\MetaVoiceType` until removed by the user.

Network access is limited to catalog model URLs, optional GitHub update checks, and Discord's local named-pipe RPC when that opt-in integration is configured. Discord authorization is not embedded; unavailable approval never blocks recording.
