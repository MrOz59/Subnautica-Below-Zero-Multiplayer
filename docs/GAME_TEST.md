# First in-game smoke test (Windows and Linux/Proton)

This procedure validates **loading the independent BepInEx plugin**, basic
game lifecycle detection, and optionally the signatures and return-time
diagnostics of the game construction hooks. **Co-op gameplay is not yet
implemented.** There is no requirement for a second player or an online
server.

The process never modifies Subnautica's native save files, downloads game
assemblies, changes store authentication or installs the original BOT Benson
mod. The game itself can still write saves normally, so use a disposable test
save and back up anything important.

## Prerequisites

1. Install a legitimate **Windows** Subnautica: Below Zero copy through Steam.
   On Linux, use the Windows game through Steam Proton, not a native Linux
   executable. The game folder must contain `SubnauticaZero.exe` and
   `SubnauticaZero_Data/Managed/Assembly-CSharp.dll`.
2. Install the **Windows x64 BepInEx 5** loader in that folder manually.
   `BepInEx/core/BepInEx.dll`, `BepInEx/core/0Harmony.dll` and
   `winhttp.dll` must already be present. Do not install a Linux-native
   BepInEx build into the Windows/Proton game.
3. Install Python 3 (and Tkinter for the GUI) plus the **.NET SDK**, which
   must be able to target .NET Framework 4.7.2. Build dependencies may be
   restored from NuGet by the normal SDK process.
4. Close the game before installing DLLs. Avoid running the original
   multiplayer mod during this diagnostic session.

## Prepare a safe solo test

Run these commands from a checkout of the `rewrite/belowzero-coop` branch.
Replace `GAME_DIR` with your installed game folder.

Windows (PowerShell):

```powershell
py -3 launcher/launcher.py prepare-test --game-dir "C:\Path\To\SubnauticaZero"
py -3 launcher/launcher.py doctor --game-dir "C:\Path\To\SubnauticaZero"
```

Linux (Steam + Proton):

```bash
python3 launcher/launcher.py prepare-test --game-dir "/path/to/steamapps/common/SubnauticaZero"
python3 launcher/launcher.py doctor --game-dir "/path/to/steamapps/common/SubnauticaZero"
```

The preparation command:

- builds the **actual net472 plugin** against game and BepInEx DLLs found in
  your installation (not the CI's fake-Unity stubs);
- installs our plugin and protocol DLLs into
  `BepInEx/plugins/BelowZeroCoop/`, backing up differing versions;
- creates/updates only our plugin's
  `BepInEx/config/dev.belowzerocoop.plugin.cfg`, backing up the old file
  if changed, while preserving unrelated settings;
- enables `ScanGameHooksOnStartup = true` for read-only reflection;
- sets `EnableProbe = false`, `EnableMovementPreview = false`, and both
  Harmony observation options to `false` by default.

Nothing connects to the development UDP server in this default profile.
For subsequent configuration-only runs, `--no-build` reuses the already
compiled Release DLLs.

For a GUI workflow, run `python3 launcher/launcher.py` (or `py -3` on
Windows), select the game folder, then click **Prepare solo test**.

## Launch and verify

On **Linux Proton**, set the game's Steam launch options to:

```text
WINEDLLOVERRIDES="winhttp.dll=n,b" %command%
```

On Windows, use the ordinary Steam launch options; no Proton override is
needed. The launcher does not modify these options for you.

1. Launch the game from Steam.
2. Reach the main menu, load a test save or start a throwaway world, and
   spend a few seconds in the actual gameplay scene.
3. Exit normally.
4. Retrieve the relevant diagnostic lines:

```bash
python3 launcher/launcher.py test-report --game-dir "/path/to/SubnauticaZero"
```

The same command works in PowerShell with `py -3` and a Windows game path.
Alternatively press **Test log** in the launcher GUI.

Expected BepInEx log messages include:

- `BelowZeroCoop loaded`: plugin entry point started.
- `[GameTest] Startup: hookScan=True ... udp=False`: safe test profile.
- `[HookScan]`: method/signature matches and missing/ambiguous API details.
- `[GameTest] Player.main available`: runtime reached an in-game scene.

If the plugin fails to load, inspect
`<game>/BepInEx/LogOutput.log` and check BepInEx's version, loader setup
and .NET plugin compilation. The reporter prints a clear message if the
BepInEx log does not exist or contains no BelowZeroCoop lines.

## Optional: observe construction/deconstruction after the first test

Once basic startup and gameplay are stable, close the game and opt in
independently (the hooks are **not** verified against every actual game
version):

```bash
python3 launcher/launcher.py prepare-test --game-dir "/path/to/SubnauticaZero" --no-build --observe-builds
```

This adds passive Postfix diagnostics for `Constructable.Construct` and
`BaseGhost.Finish`. Build/deconstruct small test objects in a disposable
save, exit and inspect `[BuildObserve]` lines.

For removal return-time diagnostics, use:

```bash
python3 launcher/launcher.py prepare-test --game-dir "/path/to/SubnauticaZero" --no-build --observe-builds --observe-deconstruction
```

A return from `DeconstructAsync` is **not proof that a coroutine finished**
or that a world object was deleted. No world-state persistence, game save
writes, network replication or real multiplayer is enabled by these hooks.
If you see a crash or conflicting mod behavior, rerun `prepare-test
--no-build` without observation flags to restore the scan-only profile.

## What to send back after testing

The output of `test-report` is the best starting point, together with:
platform (Windows or Proton), game version, BepInEx version, whether the
menu/gameplay loaded, and which test actions you performed. If startup fails,
the relevant error/exception from the full BepInEx log can help. Review
logs for personal information or file paths before sharing.

This checklist verifies in-game diagnostics, **not playable multiplayer**.
