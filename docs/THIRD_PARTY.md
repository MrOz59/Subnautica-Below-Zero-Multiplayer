# Third-party dependencies and provenance

- Subnautica: Below Zero: owned game installation, not included.
- BepInEx: external plugin loader, distributed under its own LGPL terms.
  Install an appropriate copy independently.
- Unity managed assemblies: game-provided compile references, never committed.
- Steam: external store / launcher, used for normal game startup.
- Python 3 / Tkinter: standard runtime / desktop GUI libraries.
- .NET 8 SDK: for dedicated server and builds.

The original BOT Benson public repository explicitly restricts third-party
copying and modification and warns that the public source may lag its private
development. Our code in this branch is authored separately. Do not transplant
that mod's source, assets, releases, binaries or protocol verbatim.

Project license for newly authored files is pending the owner's decision.
