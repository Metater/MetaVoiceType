# Privacy

MetaVoiceType is designed for local speech processing.

- Microphone audio is processed locally by Vosk and, during dictation, Nemotron.
- No microphone audio or transcript is uploaded by MetaVoiceType.
- Ordinary diagnostic logs contain lifecycle, model, timing, queue, and error data—not audio and not transcript bodies.
- Raw PCM exists only as crash-recovery data for an active or interrupted session. It is removed after the corresponding transcript is safely stored.
- Settings and the newest 100 history records remain under `%LOCALAPPDATA%\MetaVoiceType` until the user removes them.

Network access is used for model downloads and optional update checks. Model requests go directly to the artifact URLs in the bundled catalogs; updates use the public GitHub release source. Clipboard contents are handled locally by TextCopy and are not logged.

Vosk is always listening locally for the six configured control phrases while its model is active. This is independent of dictation recording. Nemotron receives audio only after recording starts and stops receiving new audio when the session stops.
