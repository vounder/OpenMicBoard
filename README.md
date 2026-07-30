# OpenMicBoard

<p align="center">
  <img src="src/OpenMicBoard.App/Assets/BrandMark.png" width="144" alt="OpenMicBoard waveform mark">
</p>

OpenMicBoard is a local Windows soundboard that routes clips to a virtual audio device for use as a microphone in communication software.

## Requirements

- Windows 11, version 24H2 or later
- x64 or ARM64 Windows
- A virtual audio-cable driver or other virtual output device of your choice

OpenMicBoard does not bundle, endorse, or require a particular virtual-cable product.

## Quick start

1. Install and configure a virtual audio device using its vendor's instructions.
2. In OpenMicBoard, choose that device as the virtual output.
3. Optionally select your physical microphone in OpenMicBoard so voice and clips share the route.
4. In your call, streaming, or recording application, select the corresponding virtual-cable input as the microphone.
5. Add WAV, MP3, or OGG clips, then trigger them from the board or assigned hotkeys.

Leave the microphone selector empty for a sound-only route. Check levels before joining a call; a clip can be much louder than expected.

## Imports and storage

- **Managed import** copies a clip into OpenMicBoard's private library. It remains available if the original is moved or deleted. The library uses opaque file names so the original source path is not retained for the managed asset.
- **Linked import** keeps a reference to the existing file. It avoids a duplicate, but the tile becomes unavailable if the file is moved, renamed, or deleted.

Choose managed imports for portable, self-contained boards and linked imports for media you maintain elsewhere.

## Privacy and network behavior

OpenMicBoard is designed for local use. Audio processing, library storage, device selection, and hotkey handling happen on the device. It does not require an account or cloud service. Update checks are opt-in; keeping them disabled leaves the application offline. See [PRIVACY.md](PRIVACY.md) and [docs/privacy-and-data.md](docs/privacy-and-data.md).

## Unsigned downloads

Early or community builds may be unsigned. Windows SmartScreen can therefore show a warning. Only run builds obtained from a source you trust, verify the release information when available, and prefer building from this repository when you need an independently verifiable binary.

## Build from source

Install the .NET SDK and the Windows App SDK prerequisites appropriate for this solution, then run:

```powershell
dotnet restore OpenMicBoard.sln
dotnet build OpenMicBoard.sln -c Release
dotnet test OpenMicBoard.sln -c Release
```

The release script cross-publishes self-contained x64 and ARM64 packages:

```powershell
./scripts/Build-Release.ps1 -Version 1.0.0
```

See [docs/architecture.md](docs/architecture.md) for the project layout and
[docs/release-checklist.md](docs/release-checklist.md) for the automated and
hardware-dependent release gates.

## Community and support

Read [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), [SECURITY.md](SECURITY.md), and [SUPPORT.md](SUPPORT.md) before participating.

## License

OpenMicBoard is licensed under the GNU General Public License, version 3 only. See [LICENSE](LICENSE).
