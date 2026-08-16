# Privacy

- Vosk processes microphone frames locally whenever its selected command model is active.
- Parakeet receives audio locally only during recording; one second of volatile pre-roll is kept solely to preserve speech immediately following Start/Continue.
- No MetaVoiceType service receives microphone audio, commands, transcripts, clipboard contents, settings, or history.
- Recovery PCM exists below `%LOCALAPPDATA%\MetaVoiceType\Recovery` until corrected transcript history is committed, then is deleted. Interrupted segments remain for local recovery.
- Logs contain lifecycle, provider, timing, queue, and error data—not transcript bodies or audio.
- Settings and the newest 100 logical transcripts stay below `%LOCALAPPDATA%\MetaVoiceType` until removed by the user.
- Transcript timestamps are stored canonically in UTC and converted to the current Windows time zone only for display. Whitespace-only results are never stored, copied, or pasted.
- Word replacements run locally before display/history/copy/paste.
- Recording-event and custom keyboard actions synthesize only the configured keys. MetaVoiceType does not inspect Discord or another target application's state.

Network access is limited to the exact pinned model/runtime URLs and optional GitHub application-update checks/downloads. Every model archive has an expected byte count and SHA-256; an invalid temporary download is deleted and never activated.
