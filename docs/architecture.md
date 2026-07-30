# Architecture

OpenMicBoard separates its application interface, core domain model, persistence, and Windows audio integration.

- **OpenMicBoard.App** presents the Windows UI, user actions, and global hotkey integration.
- **OpenMicBoard.Core** contains sound assets, pads, and application-facing abstractions.
- **OpenMicBoard.Infrastructure** persists board state and provides managed and linked asset handling.
- **OpenMicBoard.Audio.Windows** discovers Windows endpoints and routes playback to the selected virtual output.

The selected virtual output is intentionally independent of a specific cable driver. This lets users choose any compatible Windows virtual-audio endpoint and select its paired input in their communication application.

Audio and board operations remain local. The architecture does not depend on a server, account, or cloud library.
