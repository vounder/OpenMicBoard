# Architecture

OpenMicBoard separates its application interface, core domain model, persistence, and Windows audio integration.

- **OpenMicBoard.App** presents the Windows UI, user actions, and global hotkey integration.
- **OpenMicBoard.Core** contains sound assets, pads, and application-facing abstractions.
- **OpenMicBoard.Infrastructure** persists board state and provides managed and linked asset handling.
- **OpenMicBoard.Audio.Windows** discovers Windows endpoints and routes playback to the selected virtual output.

The selected virtual output is intentionally independent of a specific cable driver. This lets users choose any compatible Windows virtual-audio endpoint and select its paired input in their communication application.

Each pad has its own playback identity, independent of the underlying sound
asset. The fixed-voice mixer publishes deduplicated start and stop transitions
for that identity. The Windows audio engine moves those transitions off the
real-time callback before the app dispatches them to the UI. This keeps stop
controls accurate without running UI work in the audio path.

The microphone branch applies configurable input gain, an 80 Hz high-pass
filter, a speech compressor/leveler, and a hysteretic noise gate before it
enters the fixed-voice mixer. Microphone and soundboard gains are independent
and can be updated live. The master limiter remains the final clipping guard.
Microphone mute is applied inside that branch before the mixer and to the
selected Windows capture endpoint, so it never stops or attenuates soundboard
voices while applications using the physical microphone are muted too.

Pad playback is duplicated to the selected private-preview endpoint without
feeding microphone audio into that preview path.

Pad names, icon tokens, and colors are additive schema-v1 metadata. Missing or
unknown appearance values resolve to safe defaults, so libraries created before
appearance customization remain readable without a destructive migration.

Audio and board operations remain local. The architecture does not depend on a server, account, or cloud library.
