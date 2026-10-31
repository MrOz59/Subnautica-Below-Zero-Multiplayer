#!/usr/bin/env python3
"""BelowZeroCoop launcher. Uses a legitimate installed game and an external mod loader."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from datetime import datetime, timezone

STEAM_APP_ID = "848450"
GAME_EXECUTABLES = ("SubnauticaZero.exe", "SubnauticaZero.x86_64")
PLUGIN_NAME = "BelowZeroCoop.Plugin.dll"
PROTOCOL_NAME = "BelowZeroCoop.Protocol.dll"
PROJECT_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_PLUGIN = PROJECT_ROOT / "src" / "BelowZeroCoop.Plugin" / "bin" / "Release" / "net472" / PLUGIN_NAME
DEFAULT_PROTOCOL = DEFAULT_PLUGIN.with_name(PROTOCOL_NAME)
STEAM_PROTON_OPTIONS = 'WINEDLLOVERRIDES="winhttp=n,b" %command%'


def config_file() -> Path:
    if os.name == "nt":
        root = Path(os.environ.get("APPDATA", str(Path.home())))
    else:
        root = Path(os.environ.get("XDG_CONFIG_HOME", str(Path.home() / ".config")))
    return root / "belowzerocoop" / "launcher.json"


def load_config() -> dict:
    path = config_file()
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def save_config(values: dict) -> None:
    path = config_file()
    path.parent.mkdir(parents=True, exist_ok=True)
    temp = path.with_name(path.name + ".tmp")
    temp.write_text(json.dumps(values, indent=2) + "\n", encoding="utf-8")
    temp.replace(path)


def parse_manifest(path: Path) -> str | None:
    try:
        content = path.read_text(encoding="utf-8-sig", errors="replace")
    except OSError:
        return None
    match = re.search(r'"installdir"\s+"([^"]+)"', content, flags=re.IGNORECASE)
    return match.group(1) if match else None


def is_game_directory(directory: Path) -> bool:
    return directory.is_dir() and any((directory / exe).is_file() for exe in GAME_EXECUTABLES)


def default_steam_roots() -> list[Path]:
    home = Path.home()
    roots = [
        home / ".local" / "share" / "Steam",
        home / ".steam" / "steam",
        home / ".var" / "app" / "com.valvesoftware.Steam" / ".local" / "share" / "Steam",
    ]
    if os.name == "nt":
        roots.extend(
            [
                Path(os.environ.get("PROGRAMFILES(X86)", "C:/Program Files (x86)")) / "Steam",
                Path(os.environ.get("PROGRAMFILES", "C:/Program Files")) / "Steam",
            ]
        )
    return roots


def steam_libraries(roots: list[Path]) -> list[Path]:
    found: list[Path] = []
    seen: set[str] = set()
    for root in roots:
        possibilities = [root]
        manifest = root / "steamapps" / "libraryfolders.vdf"
        try:
            data = manifest.read_text(encoding="utf-8-sig", errors="replace")
        except OSError:
            data = ""
        for raw in re.findall(r'"path"\s+"([^"]+)"', data, flags=re.IGNORECASE):
            possibilities.append(Path(raw.replace("\\\\", "\\")))
        for directory in possibilities:
            normalized = str(directory.expanduser().resolve(strict=False))
            if normalized not in seen:
                found.append(directory.expanduser())
                seen.add(normalized)
    return found


def detect_installations(roots: list[Path] | None = None) -> list[Path]:
    candidates: list[Path] = []
    seen: set[str] = set()
    for library in steam_libraries(roots if roots is not None else default_steam_roots()):
        common = library / "steamapps" / "common"
        install_name = parse_manifest(library / "steamapps" / ("appmanifest_" + STEAM_APP_ID + ".acf"))
        names = ([install_name] if install_name else []) + ["SubnauticaZero", "Subnautica Below Zero"]
        for name in names:
            path = common / name
            key = str(path.resolve(strict=False))
            if key not in seen and is_game_directory(path):
                seen.add(key)
                candidates.append(path)
    return candidates


def get_game_directory(explicit: str | None) -> Path | None:
    if explicit:
        return Path(explicit).expanduser()
    saved = load_config().get("game_dir")
    if isinstance(saved, str) and is_game_directory(Path(saved)):
        return Path(saved)
    detected = detect_installations()
    return detected[0] if detected else None


def doctor(game_dir: Path | None) -> tuple[list[str], bool]:
    messages: list[str] = []
    if game_dir is None or not is_game_directory(game_dir):
        return ["ERROR: Game directory not found. Select the legitimate game installation."], False

    messages.append("Game: " + str(game_dir))
    core = game_dir / "BepInEx" / "core" / "BepInEx.dll"
    proxy = game_dir / "winhttp.dll"
    plugin = game_dir / "BepInEx" / "plugins" / "BelowZeroCoop" / PLUGIN_NAME
    protocol = plugin.with_name(PROTOCOL_NAME)

    if core.is_file():
        messages.append("OK: BepInEx core detected.")
    else:
        messages.append("MISSING: BepInEx core. Install Windows x64 BepInEx for Proton/Windows.")

    if proxy.is_file():
        messages.append("OK: winhttp.dll loader proxy detected.")
    else:
        messages.append("MISSING: winhttp.dll loader proxy.")

    if plugin.is_file() and protocol.is_file():
        messages.append("OK: BelowZeroCoop plugin and protocol assemblies found.")
    else:
        messages.append("MISSING: BelowZeroCoop plugin and/or protocol. Build and install them.")

    if sys.platform.startswith("linux"):
        messages.append("Linux/Proton Steam launch options: " + STEAM_PROTON_OPTIONS)

    return messages, core.is_file() and proxy.is_file() and plugin.is_file() and protocol.is_file()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(64 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def install_plugin(game_dir: Path, plugin_file: Path, protocol_file: Path, force: bool = False) -> list[str]:
    if not is_game_directory(game_dir):
        raise ValueError("Selected path is not a Subnautica Below Zero installation.")
    if not (game_dir / "BepInEx" / "core" / "BepInEx.dll").is_file():
        raise ValueError("Install BepInEx first; the launcher does not download or bundle third-party files.")
    if not (game_dir / "winhttp.dll").is_file():
        raise ValueError("BepInEx loader proxy winhttp.dll is missing.")

    sources = ((plugin_file, PLUGIN_NAME), (protocol_file, PROTOCOL_NAME))
    for src, expected in sources:
        if not src.is_file() or src.name != expected:
            raise ValueError("Expected compiled assembly: " + expected)

    target_dir = game_dir / "BepInEx" / "plugins" / "BelowZeroCoop"
    target_dir.mkdir(parents=True, exist_ok=True)
    results: list[str] = []
    for source, name in sources:
        target = target_dir / name
        if target.exists():
            if sha256(source) == sha256(target):
                results.append("Unchanged: " + name)
                continue
            if not force:
                raise FileExistsError("Different version already installed: " + str(target) +
                                      " (use --force to make a backup and update)")
            suffix = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
            backup = target.with_name(target.name + "." + suffix + ".bak")
            shutil.copy2(target, backup)
            results.append("Backup: " + str(backup))
        shutil.copy2(source, target)
        results.append("Installed: " + str(target))
    return results


def launch_steam() -> None:
    if sys.platform.startswith("win"):
        os.startfile("steam://rungameid/" + STEAM_APP_ID)  # type: ignore[attr-defined]
        return
    steam = shutil.which("steam")
    if steam:
        subprocess.Popen([steam, "-applaunch", STEAM_APP_ID], start_new_session=True)
        return
    if shutil.which("flatpak"):
        subprocess.Popen(
            ["flatpak", "run", "com.valvesoftware.Steam", "steam://rungameid/" + STEAM_APP_ID],
            start_new_session=True,
        )
        return
    raise RuntimeError("Steam not found; launch the game from your Steam library.")


def start_server() -> None:
    server = PROJECT_ROOT / "src" / "BelowZeroCoop.Server" / "bin" / "Debug" / "net8.0" / "BelowZeroCoop.Server.dll"
    release = PROJECT_ROOT / "src" / "BelowZeroCoop.Server" / "bin" / "Release" / "net8.0" / "BelowZeroCoop.Server.dll"
    selected = release if release.is_file() else server
    if not selected.is_file():
        raise FileNotFoundError("Build the server first: dotnet build src/BelowZeroCoop.Server")
    if not shutil.which("dotnet"):
        raise RuntimeError(".NET 8 runtime is required to start the development server.")
    subprocess.Popen(["dotnet", str(selected)], cwd=str(PROJECT_ROOT), start_new_session=True)


def gui() -> None:
    try:
        import tkinter as tk
        from tkinter import filedialog, messagebox, ttk
    except ImportError as ex:
        raise RuntimeError("Tkinter unavailable. Install python3-tk or use the command line.") from ex

    root = tk.Tk()
    root.title("BelowZeroCoop - Developer Preview")
    root.minsize(760, 500)
    root.geometry("850x540")
    frame = ttk.Frame(root, padding=16)
    frame.pack(fill="both", expand=True)
    value = ttk.Entry(frame)
    value.insert(0, str(get_game_directory(None) or ""))
    ttk.Label(frame, text="Game installation directory:").pack(anchor="w")
    value.pack(fill="x", pady=(4, 8))
    output = tk.Text(frame, height=18, wrap="word", state="disabled")
    output.pack(fill="both", expand=True, pady=(8, 0))

    def log(message: str) -> None:
        output.configure(state="normal")
        output.insert("end", message + "\n")
        output.see("end")
        output.configure(state="disabled")

    def folder() -> Path:
        return Path(value.get()).expanduser()

    def browse() -> None:
        selection = filedialog.askdirectory(title="Select Subnautica Below Zero folder")
        if selection:
            value.delete(0, "end")
            value.insert(0, selection)

    def check() -> None:
        messages, _ = doctor(folder())
        for line in messages:
            log(line)
        if is_game_directory(folder()):
            save_config({"game_dir": str(folder())})

    def install() -> None:
        try:
            for line in install_plugin(folder(), DEFAULT_PLUGIN, DEFAULT_PROTOCOL, force=False):
                log(line)
        except (OSError, ValueError) as ex:
            messagebox.showerror("Installation failed", str(ex))

    def run_action(action) -> None:
        try:
            action()
        except (OSError, RuntimeError) as ex:
            messagebox.showerror("BelowZeroCoop", str(ex))

    bar = ttk.Frame(frame)
    bar.pack(fill="x", pady=(6, 6), before=output)
    ttk.Button(bar, text="Browse", command=browse).pack(side="left", padx=(0, 6))
    ttk.Button(bar, text="Diagnose", command=check).pack(side="left", padx=(0, 6))
    ttk.Button(bar, text="Install built plugin", command=install).pack(side="left", padx=(0, 6))

    actions = ttk.Frame(frame)
    actions.pack(fill="x", pady=(6, 4), before=output)
    ttk.Button(actions, text="Launch via Steam", command=lambda: run_action(launch_steam)).pack(side="left", padx=(0, 6))
    ttk.Button(actions, text="Start dev server", command=lambda: run_action(start_server)).pack(side="left", padx=(0, 6))
    ttk.Button(
        actions,
        text="Copy Proton options",
        command=lambda: (root.clipboard_clear(), root.clipboard_append(STEAM_PROTON_OPTIONS),
                         log("Copied: " + STEAM_PROTON_OPTIONS)),
    ).pack(side="left")

    log("Independent mod development preview. No DRM changes or original mod files included.")
    root.mainloop()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="BelowZeroCoop - independent Linux/Windows launcher")
    commands = parser.add_subparsers(dest="command")
    commands.add_parser("gui", help="Show graphical launcher (requires Tkinter)")
    d = commands.add_parser("doctor", help="Check game and loader setup")
    d.add_argument("--game-dir")
    set_dir = commands.add_parser("set-game", help="Save game directory")
    set_dir.add_argument("game_dir")
    commands.add_parser("steam-options", help="Print Proton launch options")
    ins = commands.add_parser("install", help="Install built original assemblies")
    ins.add_argument("--game-dir")
    ins.add_argument("--plugin", type=Path, default=DEFAULT_PLUGIN)
    ins.add_argument("--protocol", type=Path, default=DEFAULT_PROTOCOL)
    ins.add_argument("--force", action="store_true", help="Back up and replace existing mod DLLs")
    commands.add_parser("launch", help="Launch the legitimately installed game via Steam")
    commands.add_parser("start-server", help="Start the local development-only UDP server")
    args = parser.parse_args(argv)

    try:
        if args.command in (None, "gui"):
            gui()
        elif args.command == "doctor":
            lines, ok = doctor(get_game_directory(args.game_dir))
            print("\n".join(lines))
            return 0 if ok else 1
        elif args.command == "set-game":
            path = Path(args.game_dir).expanduser()
            if not is_game_directory(path):
                raise ValueError("Not a valid Below Zero installation: " + str(path))
            save_config({"game_dir": str(path.resolve())})
            print("Saved: " + str(path.resolve()))
        elif args.command == "steam-options":
            print(STEAM_PROTON_OPTIONS)
        elif args.command == "install":
            game = get_game_directory(args.game_dir)
            if game is None:
                raise ValueError("Game directory not found. Specify --game-dir.")
            print("\n".join(install_plugin(game, args.plugin, args.protocol, args.force)))
        elif args.command == "launch":
            launch_steam()
        elif args.command == "start-server":
            start_server()
        return 0
    except (OSError, RuntimeError, ValueError) as ex:
        print("ERROR: " + str(ex), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
