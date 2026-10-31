# BelowZeroCoop (working name)

New, independently implemented co-op project for Subnautica: Below Zero.
This branch has a fresh file tree; it does not include code from the original
BOT Benson mod. Original project's restrictive notice is not a license grant.

## Status - engineering scaffold, NOT playable multiplayer

- A cross-platform Python launcher with a small Tkinter desktop UI.
- Detection of installed Steam copies, including custom Steam libraries.
- BepInEx setup checks and safe deployment of our compiled plugin assemblies.
- Linux via Steam + Proton, with a documented winhttp DLL override.
- Independent version 2 UDP control and movement packet formats.
- A .NET 8 development server with expiring peer sessions, movement
  validation, server-assigned peer IDs, LAN relay and late-join snapshots.
- Best-effort peer departure notifications and idle expiry, including
  cleanup of temporary remote capsules.
- Portable, jitter-adaptive interpolation with delayed-packet rejection.
- A BepInEx 5 Unity Mono plugin that can optionally send a local position
  snapshot at 20 Hz and render other peers as noninteractive debug capsules.
- Installer rollback, protocol and real UDP integration tests in CI.

NOT implemented: actual player avatars, animation/vehicle/world replication,
inventory, saves, authentication, real player spawning, authoritative gameplay,
discovery, automatic BepInEx downloads, Epic launching or production security.
Remote capsules are temporary debug geometry, not true multiplayer characters.

## Quick start - Linux (Steam/Proton)

1. Own/install Subnautica: Below Zero through Steam (appid 848450).
2. Install **Windows x64** BepInEx 5 into the Windows game directory (Proton runs the Windows game).
3. In Steam, set the game's launch options to:
   WINEDLLOVERRIDES="winhttp.dll=n,b" %command%
4. Install Python 3, including Tkinter (python3-tk on many Linux distros), and .NET SDK 8.
5. Build the plugin with local BepInEx and the game's Unity Mono assemblies:
   dotnet build src/BelowZeroCoop.Plugin/BelowZeroCoop.Plugin.csproj -c Release -p:BepInExCore=/game/BepInEx/core -p:GameManaged=/game/SubnauticaZero_Data/Managed
6. Run the launcher: python3 launcher/launcher.py
7. In the launcher, select the game folder, diagnose, install the built DLLs,
   then launch the legitimately installed game via Steam.
8. For a network probe, build the test server:
   dotnet build src/BelowZeroCoop.Server/BelowZeroCoop.Server.csproj
   python3 launcher/launcher.py start-server
   Then set BepInEx/config/dev.belowzerocoop.plugin.cfg: EnableProbe = true
   The movement preview is separately disabled by default. See docs/LINUX.md
   for optional LAN-only development preview instructions.

Linux instructions in docs/LINUX.md.

## Windows

Use Windows x64 BepInEx 5 and the same compiled plugin. Python + Tkinter,
.NET 8 and Steam are needed for the developer preview. Launch normally via
Steam. No custom binary patching or store checks.

## Tests

python3 -m unittest discover -s launcher/tests -v
dotnet run --project src/BelowZeroCoop.SmokeTests
dotnet build src/BelowZeroCoop.Server -c Release

The CI compiles our plugin *source* against temporary stub APIs. This does not
prove compatibility with real Unity or BepInEx binaries.

Real plugin compilation is intentionally a separate local build step because
BepInEx and Unity/game assemblies are not bundled in the repository.

## Architecture

- launcher/: our launcher and local environment verification.
- src/BelowZeroCoop.Protocol/: original network wire format.
- src/BelowZeroCoop.Server/: development server, loopback by default.
- src/BelowZeroCoop.Plugin/: BepInEx mod entry point.
- src/BelowZeroCoop.SmokeTests/: packet format and interpolation tests.
- src/BelowZeroCoop.Plugin.CompileCheck/: CI-only fake game API to syntax-check our plugin; never ship it.
- docs/: Linux details, planned milestones and third-party dependencies.

Use this branch for all new development. No game or BOT Benson code is copied.
No game authentication, DRM or store identity is bypassed.

## Licensing and third-party code

No distribution license has been selected for this new project yet.
BepInEx and Unity/game files are external dependencies. The original source
tree at the repository's main branch has different authorship and restrictions:
those files are not included in this branch.

See docs/ARCHITECTURE.md, docs/ROADMAP.md, and
[the review of the original project's first 100 issues](docs/UPSTREAM_ISSUES_1-100.md).

The CI includes regression cases inspired by early upstream reports: concurrent
joins (#63), duplicate debug avatars (#5), preserving singleplayer when networking
is disabled (#64), and deterministic packet loss, jitter, duplication and
reordering (#83). These are new tests, not a port of the original mod.
