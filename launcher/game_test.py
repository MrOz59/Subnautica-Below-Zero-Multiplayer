"""Safe, local in-game diagnostics for an installed, legitimate Below Zero copy.

Does not download game files, change stores, contact a server, modify native
saves or install additional mods. The caller validates the selected game.
"""
from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
import os
import re
import shutil
import subprocess
import tempfile

CONFIG_NAME = "dev.belowzerocoop.plugin.cfg"
PLUGIN_LOG_PREFIXES = (
    "[HookScan]", "[BuildObserve]", "[GameTest]",
    "BelowZeroCoop loaded", "BelowZeroCoop development server",
)
SECTION = re.compile(r"^\s*\[([^\]]+)\]\s*$")
ASSIGNMENT = re.compile(r"^(\s*)([A-Za-z][A-Za-z0-9]*)\s*=(.*)$")


def build_game_plugin(game_dir: Path, project_root: Path) -> tuple[Path, Path]:
    """Build Windows/Proton net472 plugin against the user's own game DLLs."""
    game_dir = Path(game_dir)
    core = game_dir / "BepInEx" / "core"
    managed = game_dir / "SubnauticaZero_Data" / "Managed"
    required = (core / "BepInEx.dll", core / "0Harmony.dll",
                managed / "Assembly-CSharp.dll",
                managed / "UnityEngine.CoreModule.dll",
                game_dir / "SubnauticaZero.exe")
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise FileNotFoundError(
            "Cannot compile the game plugin; missing installed Windows Mono game/BepInEx files:\n"
            + "\n".join(missing)
            + "\nUse the Windows x64 BepInEx 5 loader; Proton runs the Windows game."
        )
    sdk = shutil.which("dotnet")
    if not sdk:
        raise RuntimeError("Install the .NET SDK (not only the runtime) to build the plugin.")
    project = project_root / "src" / "BelowZeroCoop.Plugin" / "BelowZeroCoop.Plugin.csproj"
    args = [
        sdk, "build", str(project), "-c", "Release",
        "-p:BepInExCore=" + str(core.resolve()),
        "-p:GameManaged=" + str(managed.resolve()),
    ]
    # Never evaluate a shell command or execute a supplied binary.
    subprocess.run(args, cwd=project_root, check=True)
    output = project.parent / "bin" / "Release" / "net472"
    plugin = output / "BelowZeroCoop.Plugin.dll"
    protocol = output / "BelowZeroCoop.Protocol.dll"
    if not (plugin.is_file() and protocol.is_file()):
        raise FileNotFoundError("Build completed without the required plugin/protocol assemblies.")
    return plugin, protocol


def _update_ini(text: str, values: dict[str, dict[str, str]]) -> str:
    """Preserve unrelated BepInEx lines; replace only our owned setting keys."""
    lines = text.splitlines(keepends=True)
    newline = "\r\n" if "\r\n" in text else "\n"
    result: list[str] = []
    found: set[tuple[str, str]] = set()
    active = ""
    seen_sections: set[str] = set()

    def append_missing(section: str) -> None:
        if section not in values:
            return
        for key, value in values[section].items():
            if (section, key) not in found:
                if result and not result[-1].endswith(("\r", "\n")):
                    result.append(newline)
                result.append(f"{key} = {value}{newline}")
                found.add((section, key))

    for line in lines:
        header = SECTION.match(line.rstrip("\r\n"))
        if header:
            append_missing(active)
            active = header.group(1).strip()
            seen_sections.add(active)
            result.append(line)
            continue
        match = ASSIGNMENT.match(line)
        if match and active in values and match.group(2) in values[active]:
            key = match.group(2)
            if (active, key) not in found:
                result.append(f"{match.group(1)}{key} = {values[active][key]}{newline}")
                found.add((active, key))
            # Duplicate managed keys are discarded to prevent BepInEx ambiguity.
        else:
            result.append(line)
    append_missing(active)
    for section in values:
        if section not in seen_sections:
            if result and not result[-1].endswith(("\r", "\n")):
                result.append(newline)
            if result and result[-1].strip():
                result.append(newline)
            result.append(f"[{section}]{newline}")
            append_missing(section)
    return "".join(result)


def configure_solo_test(game_dir: Path, *, observe_builds: bool = False,
                        observe_deconstruction: bool = False) -> list[str]:
    """Enable local diagnostics with network/probe disabled, preserving config."""
    cfg = Path(game_dir) / "BepInEx" / "config" / CONFIG_NAME
    values = {
        "Network": {"EnableProbe": "false"},
        "Development": {
            "ScanGameHooksOnStartup": "true",
            "ObserveBuildingHooks": str(observe_builds).lower(),
            "ObserveDeconstructionHooks": str(observe_deconstruction).lower(),
            "EnableMovementPreview": "false",
        },
    }
    old = cfg.read_text(encoding="utf-8-sig") if cfg.is_file() else ""
    updated = _update_ini(old, values)
    if updated == old:
        return ["Already configured: " + str(cfg)]
    cfg.parent.mkdir(parents=True, exist_ok=True)
    results: list[str] = []
    if cfg.is_file():
        # A timestamp with microseconds avoids overwriting older backups.
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        backup = cfg.with_name(cfg.name + "." + stamp + ".bak")
        shutil.copy2(cfg, backup)
        results.append("Previous config backed up: " + str(backup))
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", newline="",
            prefix=".bzc-config-", suffix=".tmp", dir=cfg.parent,
            delete=False,
        ) as target:
            temporary = Path(target.name)
            target.write(updated)
            target.flush()
            os.fsync(target.fileno())
        if cfg.exists():
            shutil.copymode(cfg, temporary)
        os.replace(temporary, cfg)
        results.append("Solo diagnostics configured: " + str(cfg))
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()
    return results


def game_test_report(game_dir: Path, limit: int = 80) -> list[str]:
    """Read-only summary of plugin diagnostics from BepInEx's local logs."""
    if not 1 <= limit <= 500:
        raise ValueError("The report line limit must be between 1 and 500.")
    game_dir = Path(game_dir)
    candidates = [
        game_dir / "BepInEx" / "LogOutput.log",
        game_dir / "LogOutput.log",
    ]
    log = next((p for p in candidates if p.is_file()), None)
    if log is None:
        return [
            "BepInEx log not found. Start the game via Steam, then exit normally.",
            "Expected: " + str(candidates[0]),
            "Check that the BepInEx Windows loader actually started.",
        ]
    # Only keep bounded output; do not read the entire log into memory.
    from collections import deque
    extracted: deque[str] = deque(maxlen=limit)
    with log.open("r", encoding="utf-8-sig", errors="replace") as handle:
        for raw in handle:
            if any(marker in raw for marker in PLUGIN_LOG_PREFIXES):
                extracted.append(raw.rstrip("\r\n")[:1200])
    if not extracted:
        return [
            "No BelowZeroCoop diagnostic lines found in " + str(log),
            "Confirm the plugin loaded and the game was started after test preparation.",
        ]
    return ["Log: " + str(log), "Recent BelowZeroCoop diagnostics:"] + list(extracted)
