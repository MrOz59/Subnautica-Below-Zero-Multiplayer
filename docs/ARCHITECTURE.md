# Architecture

Clean implementation with a fresh file tree. The earlier mod may inform
requirements and gameplay research; its code, assets, protocols, UI and
distribution components must not be copied without permission.

## Boundaries

Launcher (Python 3, Linux/Windows)
  - Discovers legitimate installations via Steam app manifests.
  - Validates BepInEx and copies only our build outputs.
  - Launches using Steam, retaining platform ownership/login.
  - Gives actionable diagnostics; no DRM workarounds.

Plugin (C#, BepInEx 5 + Unity Mono)
  - Unity lifecycle entry point.
  - Nonblocking UDP receive with a per-frame packet budget.
  - Optional position capture at 20 Hz via Player.main (in game).
  - Debug-only remote capsules with the portable movement interpolator.
  - Future: proper remote prefabs, animation, world replication and events.
  - Unity objects must only be manipulated from Unity's main thread.

Protocol (C#, .NET Standard 2.0)
  - Standalone codec usable by the mod and server.
  - Format v2: ASCII BZC0 (4 bytes), version (2), type (1),
    little-endian uint sequence (4) = 10 bytes.
  - Type 1 = Ping; 2 = Pong; 3 = Hello; 4 = HelloAck.
  - Control type 6 = Welcome (server-assigned ID); 7 = PeerLeft;
    8 = Goodbye (best-effort disconnect).
  - Type 5 = movement (34 bytes): 10-byte header, uint PeerId, float64
    sender monotonic timestamp, three float32 world coordinates.
  - Uploaded movement uses PeerId=0; only the server stamps peer IDs.
  - Rejects invalid versions, unknown types, bad lengths, nonfinite data,
    and coordinates outside +/-100,000 units.
  - Pure .NET interpolation buffer accepts monotonic movement snapshots,
    rejects stale packets, and clamps extrapolation for high RTT.
  - The buffer is connected only to a disposable debug capsule, not to
    real networked characters or gameplay state.
  - No compatibility promise while in development.

Server (C#, .NET 8)
  - Development UDP probe/movement relay; listens on 127.0.0.1 by default.
  - Hello required before Ping/movement; sessions expire after 30s.
  - Sends identity on join and immediate cached movement for existing peers.
  - Best-effort PeerLeft on explicit exit and inactive-session pruning
    even when no datagrams are being received.
  - At most eight peers, 40 accepted movement samples/second per peer.
  - Rejects repeated sequence numbers, spoofed peer IDs and implausible speed.
  - No real authentication, world or gameplay authority yet.
  - Must not be publicly exposed in this state.

## Security and interoperability tasks before a multiplayer alpha

- Session-based authenticated handshake; cryptographic session keys.
- Version negotiation and protocol caps, packet length limits.
- Per-peer quotas, rate limiting and malformed packet logging.
- Host/client ownership decisions; deterministic save and replication.
- Discovery and NAT handling (LAN first).
- Reliable event channel separate from unreliable state snapshots.
- Idempotent inventory, base building and world events.
- Proper cleanup on disconnect and reconnect, network statistics.
- Tests that simulate 300 ms RTT, jitter, reorder and packet loss.

BepInEx uses LGPL components; end users must install them separately.
The game and original mod have separate licensing and are not redistributed.

## Hook discovery versus gameplay patching

The optional `GameHookCatalog` is a **read-only reflection probe** limited
to the loaded `Assembly-CSharp` assembly. It checks candidate class names,
methods and overload counts, logging `Found`, `Ambiguous`,
`TypeMissing`, `MethodMissing` or `InspectionFailed`.

The catalog contains game-facing identifiers researched from the original
mod's public Harmony patch declarations (not any of its original patch
bodies). It **does not install Harmony patches**. A real implementation
must resolve signatures against the target game version, handle coroutine
completion safely, guard singleplayer, and avoid feedback loops when the
host replays a remote game event.

References and the prioritized integration list are in
[GAME_HOOKS_RESEARCH.md](GAME_HOOKS_RESEARCH.md).
