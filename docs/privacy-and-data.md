# Privacy and local data

OpenMicBoard stores its working data locally. A managed import is copied to the private library using an opaque identifier, while a linked import stores the local path required to find the original file.

Removing a linked pad never deletes its original file. When a managed sound is
removed from its final profile, OpenMicBoard deletes only the opaque copy
inside its own private library and then removes the unused asset metadata.

Installed mode stores settings and the managed library below
`%LOCALAPPDATA%\OpenMicBoard`. Portable mode is selected only when the
`OpenMicBoard.portable` marker exists beside the executable; its data then lives
in the adjacent `data` folder. Managed asset records contain only relative opaque
file names, so a portable folder can move between compatible computers.

Profile names and each pad's display name, icon token, color value, volume, and
shortcut are stored only in the same local library index. Appearance
customization does not add network requests, external assets, or additional file
paths. Renaming a pad changes only that pad in its current profile.

Microphone boost, voice-leveling, noise-gate, and soundboard-gain preferences
are stored only in the local settings file. OpenMicBoard does not record or
retain microphone samples.
The microphone-mute state is stored in the same file. The control also sets the
standard Windows mute state on the selected capture endpoint; muting discards
captured samples in memory and does not affect saved sound clips.

Uninstalling the application intentionally leaves the installed-mode library in
place to avoid accidental loss of user audio. Delete `%LOCALAPPDATA%\OpenMicBoard`
manually when you also want to erase settings and managed clips. Removing a
portable folder removes its adjacent `data` folder with it.

Audio clips are played to the virtual output selected by the user. OpenMicBoard does not itself send clips or microphone audio to a network service. Other applications receiving the virtual device, and any installed cable driver, operate under their own settings and policies.

Update checks are optional. Disable them when you want OpenMicBoard to remain offline. See the project-wide [privacy policy](../PRIVACY.md).
