# Linux support (first-class target via Proton)

Below Zero on Steam is treated as a Windows Unity Mono game running through
Steam Play/Proton. This project does not claim a native Linux game port.

## Installation and Proton integration

1. Install the game through Steam. AppID: 848450.
2. Install the Windows x64 build of BepInEx 5 into the game directory, beside
   SubnauticaZero.exe. Do not use the Linux-native BepInEx build for Proton.
3. Steam -> Below Zero -> Properties -> Launch Options:
   WINEDLLOVERRIDES="winhttp.dll=n,b" %command%
4. Install Python 3 + Tkinter. On Debian/Ubuntu: sudo apt install python3-tk
5. Run: python3 launcher/launcher.py
6. Select the game's directory and use Diagnose. Custom Steam libraries and
   Flatpak Steam are scanned from common paths.
7. Build the plugin with local game references and install only the two
   BelowZeroCoop DLLs using the launcher.

The launcher never downloads BepInEx or patches Unity files automatically.
It does not change Steam's launch options. Copy the launch option yourself.

If Steam is installed as a Flatpak, launching will attempt to use flatpak.
Use your actual Steam installation/Proton prefix.

## Development-only network probe and movement capsules

Build the development server:
```sh
dotnet build src/BelowZeroCoop.Server/BelowZeroCoop.Server.csproj -c Release
python3 launcher/launcher.py start-server
```
By default it binds only to 127.0.0.1:27021 and writes
`~/.config/belowzerocoop/server.log` on typical Linux desktops.

To connect from another computer on a **trusted private LAN only**:
```sh
python3 launcher/launcher.py start-server --bind 0.0.0.0 --port 27021 --allow-lan
```
The server has **no authentication**. Do not forward or expose its UDP port
to the internet. Set an appropriate local firewall rule if necessary.

In the game's `BepInEx/config/dev.belowzerocoop.plugin.cfg`:
```ini
[Network]
EnableProbe = true
Host = 127.0.0.1
Port = 27021

[Development]
EnableMovementPreview = false
```
Change `Host` to the LAN server's IP for another machine. Only change
`EnableMovementPreview` to `true` for an intentional debug session.
All participating clients need the same protocol version; in-game snapshots
are then forwarded at up to 20 Hz, with other peers displayed as temporary
noninteractive capsules. These are not real player models.

Hello/HelloAck precedes Ping; sessions expire after 30 seconds.
Check `BepInEx/LogOutput.log` for plugin logs.

The launcher never installs BepInEx for you or modifies store authentication.

## Plugin build reference paths

- BepInExCore: directory containing BepInEx.dll
- GameManaged: directory containing UnityEngine.CoreModule.dll
  and Assembly-CSharp.dll
- The .NET SDK must have access to reference assemblies for net472.

Example:
dotnet build src/BelowZeroCoop.Plugin/BelowZeroCoop.Plugin.csproj -c Release -p:BepInExCore=/path/to/game/BepInEx/core -p:GameManaged=/path/to/game/SubnauticaZero_Data/Managed

If the game uses another Unity backend or changes its assembly layout, the
plugin build and loading mechanism must be reassessed. Do not copy another
mod's private DLLs into this project.

## Current limitations

- No actual co-op gameplay yet.
- Native Linux game binaries are not supported or promised.
- Epic Games launching has not been implemented.
- Game/store login remains handled by the original installation.
- Development server must not be exposed to the internet (no authentication).
- Full in-game compatibility has not been verified against a real game build.
- Proton launch flags and dependencies depend on the installed version.
