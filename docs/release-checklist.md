# Release checklist

Automated release gates cover tests, x64 and ARM64 compilation, deployable-resource checks,
an x64 launch smoke test, privacy scans, dependency auditing, SBOM generation, checksums, and
GitHub artifact attestations.

The following hardware-dependent checks must be completed manually on release candidates:

- Start the ARM64 portable build on native Windows on ARM hardware.
- Route the board through a real virtual audio cable into a communication application's
  microphone input.
- Mix a physical microphone at 48 kHz and 44.1 kHz while repeatedly playing pads.
- Unplug and reconnect the selected render, capture, and preview endpoints. Confirm that audio
  fails closed while absent and that each endpoint is reselected after it returns.
- Confirm that private preview is audible only on the chosen preview endpoint.
- Install and uninstall both architecture-matched installers from a standard user account.

Record the tested Windows build, hardware architecture, audio devices, and result in the
release notes. Do not claim a hardware check that was not performed.
