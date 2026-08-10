# Changelog

All notable changes to OpenMicBoard are documented here.

## Unreleased

- Added prominent file-browser actions and multi-file audio import.
- Added named-profile creation and per-profile pad renaming.
- Added modifier-free global shortcuts, including distinct Numpad digits and
  operators, navigation keys, and media controls.
- Added searchable Fluent icons and custom pad accent colors.
- Added per-pad removal with confirmation, profile-aware asset cleanup, and
  protection against deleting linked original files.
- Added accurate per-pad playback state for the stop controls.
- Added live microphone boost, voice leveling, an adjustable noise gate, and a
  separate soundboard boost.
- Added a persistent live microphone mute that controls both the internal mix
  and the selected Windows capture endpoint, leaving soundboard playback
  active even when Discord uses the physical microphone directly.
- Added automatic private monitoring when a sound pad is triggered.
- Fixed private preview stalling when a Windows audio endpoint crossed COM
  apartment threads during asynchronous file loading.
- Added physical-device identity checks that reject SPDIF, speakers, or
  headphones from the same hardware when accidentally selected as the virtual
  route.
- Shortened master-limiter recovery so loud clips no longer hold the
  microphone down as long after their peaks.
- Improved startup sizing and responsive layouts across different displays.
- Separated shortcut actions from managed- and linked-file status labels.

## 1.0.0 - 2026-07-30

- Initial WinUI soundboard dashboard with a dark graphite and mint visual system.
- Local managed-copy and linked-file sound libraries with atomic JSON recovery.
- Explicit virtual output, optional microphone, and separate private-preview routes.
- WAV, MP3, and OGG playback with eight fixed voices, retriggering, fades, and limiter.
- Endpoint hot-plug detection with fail-closed recovery to the same endpoint ID.
- Global keyboard shortcuts, profiles, optional autostart, and selectable buffering.
- Offline-by-default operation with consent-gated GitHub release checks.
- Self-contained x64 and ARM64 installer and portable build automation.
