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
  - Initial transport probe with a non-blocking per-frame UDP receive budget.
  - Future: world integration, player snapshot rendering, event capture.
  - Unity objects must only be manipulated from Unity's main thread.

Protocol (C#, .NET Standard 2.0)
  - Standalone codec usable by the mod and server.
  - Format v1: ASCII BZC0 (4 bytes), version (1), type (1),
    little-endian uint sequence (4) = 10 bytes.
  - Type 1 = Ping; type 2 = Pong; type 3 = Hello; type 4 = HelloAck.
  - Unknown versions and packet types rejected.
  - Pure .NET interpolation buffer accepts monotonic movement snapshots,\n    rejects stale packets, and clamps extrapolation for high RTT.\n  - The buffer is not yet connected to remote Unity player objects.\n  - No compatibility promise while in development.

Server (C#, .NET 8)
  - Development UDP Ping/Pong listener; 127.0.0.1 only by default.
  - A Hello is required before Ping; endpoint sessions expire after 30s.
  - No real authentication, authorization, world or gameplay authority yet.
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
