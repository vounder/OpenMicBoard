# Privacy and local data

OpenMicBoard stores its working data locally. A managed import is copied to the private library using an opaque identifier, while a linked import stores the local path required to find the original file.

Installed mode stores settings and the managed library below
`%LOCALAPPDATA%\OpenMicBoard`. Portable mode is selected only when the
`OpenMicBoard.portable` marker exists beside the executable; its data then lives
in the adjacent `data` folder. Managed asset records contain only relative opaque
file names, so a portable folder can move between compatible computers.

Uninstalling the application intentionally leaves the installed-mode library in
place to avoid accidental loss of user audio. Delete `%LOCALAPPDATA%\OpenMicBoard`
manually when you also want to erase settings and managed clips. Removing a
portable folder removes its adjacent `data` folder with it.

Audio clips are played to the virtual output selected by the user. OpenMicBoard does not itself send clips or microphone audio to a network service. Other applications receiving the virtual device, and any installed cable driver, operate under their own settings and policies.

Update checks are optional. Disable them when you want OpenMicBoard to remain offline. See the project-wide [privacy policy](../PRIVACY.md).
