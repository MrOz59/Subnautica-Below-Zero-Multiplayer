# Linux support (first-class target via Proton)

Below Zero on Steam is treated as a Windows Unity Mono game running through
Steam Play/Proton. This project does not claim a native Linux game port.

## Installation and Proton integration

1. Install the game through Steam. AppID: 848450.
2. Install the Windows x64 build of BepInEx 5 into the game directory, beside
   SubnauticaZero.exe. Do not use the Linux-native BepInEx build for Proton.
3. Steam -> Below Zero -> Properties -> Launch Options:
   WINEDLLOVERRIDES="winhttp=n,b" %command%
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

## Local development probe

Start the server: dotnet run --project src/BelowZeroCoop.Server
It listens only at 127.0.0.1:27021 unless explicitly changed.
Enable the plugin probe in BepInEx/config/dev.belowzerocoop.plugin.cfg.
Check BepInEx/LogOutput.log for the connection message.

## Plugin build reference paths

- BepInExCore: directory containing BepInEx.dll
- GameManaged: directory containing UnityEngine.CoreModule.dll
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
- Dedicated server must not be exposed to the internet (no auth or rate limit).
- Proton launch flags and dependencies depend on the installed version.
