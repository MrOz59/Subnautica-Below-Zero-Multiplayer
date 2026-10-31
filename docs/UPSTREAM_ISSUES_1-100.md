# Original project's first 100 issues: regression-risk audit

Source: [ismail0234/Subnautica-Below-Zero-Multiplayer issues](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues).
Reviewed issues/PR numbers **#1 through #100** (96 issues and 4 PRs: #19, #20, #71 and #75).

## Interpretation and limitations

These are **historical alpha issues**, mostly created by the original project
maintainer. Many entries are planned features (especially furniture, tools and
vehicles), **not independently reproduced bugs**. They are closed now, often
with a brief "Fixed" or "Completed" comment. A closed issue is NOT evidence
that a defect still exists, or that the code in the independent BelowZeroCoop
project is affected. This file documents requirements and regression risks,
not proprietary implementation details.

The independent codebase is not copied from the original project.

## P0: prevent at the foundation

| Source | Historical failure / requirement | BelowZeroCoop safeguard | Remaining work |
|---|---|---|---|
| [#5](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/5) | Duplicate remote player models on connection loss | Server-assigned peer identity, explicit PeerLeft, capsule dictionary indexed by peer ID | Idempotent spawn/despawn under lost/duplicated UDP messages; reconnect integration tests |
| [#17](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/17), [#63](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/63) | Loading-screen timing and simultaneous player joins | Versioned handshake, Welcome, baseline for late join, server-serial processing | Concurrent join stress test, snapshot consistency and world-ready gate |
| [#64](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/64) | Singleplayer broken while multiplayer enabled | Development preview defaults OFF, no game patch/hook used for singleplayer | Explicit singleplayer no-op regression test; preserve vanilla save behavior |
| [#21](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/21) | Auto reconnect design question (original author deferred it) | Timeout and Hello retries for diagnostic transport | Full disconnect state machine, consent-driven reconnect and ownership cleanup |
| [#83](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/83) | Network test plan: lag, drop, throttle, reorder, duplicates, tamper | Versioned frames, strict lengths, sequence validation, interpolation tests | Deterministic network-impairment harness (300ms RTT, jitter/loss/reorder), fuzz testing |
| [#4](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/4), [#56](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/56), [#66](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/66) | Multiple UDP channels, migration to UDP and layer optimization | Separate control and movement codecs, 20Hz movement, stationary heartbeat | Reliable/ordered channel for events; per-peer congestion queues; net budgets |

## P1: design before any shared-world implementation

| Source | Historical failure / requirement | Required design |
|---|---|---|
| [#77](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/77) | Construction becomes unusable after last interacting player logs out | Standalone leased object ownership with expiry/transfer; integration pending |
| [#82](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/82) | Bioreactor item persists after destroy-and-rebuild | Standalone UUID generations, tombstones, item transactions; integration pending |
| [#85](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/85) | Another player's construction missing after re-entry | Detached full snapshots, bounded incremental deltas, local JSON persistence; durable journal and game integration pending |
| [#6](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/6), [#10](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/10), [#12](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/12), [#79](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/79), [#80](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/80) | Illegal base changes, player collisions and construction remove/despawn errors | Check object preconditions and occupants on host; atomic builder transaction |
| [#78](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/78) | Quickslots broke after metadata refactor | Independent versioned quickslot metadata with explicit own-format migration and strict regression tests; game integration pending |
| [#13](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/13), [#81](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/81) | Energy production/consumption desync | Independent fixed-point power model, source tombstones, idempotent updates and restart tests; game integration pending |
| [#3](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/3), [#76](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/76) | Fabricator interaction can become stuck | Standalone Fabricator lifecycle with short-lived UI leases, repeatable output tickets and idempotent acknowledgement; authentication/game adapter pending |
| [#18](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/18) | Rejoining at last location, including base/Seatruck | Position + coordinate-space/vehicle attachment, server revision and safe spawn |
| [#52](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/52) | Furniture looks wrong for another observer | Stable object transform and scene/space identity, not camera-relative positions |
| [#70](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/70), [#72](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/72), [#74](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/74) | Expensive or inconsistent world serialization/metadata and interactions | Explicit versioned data contracts, snapshot/delta codecs, load budget |

## P2: later gameplay polish and complete feature coverage

- [#1](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/1), [#2](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/2), [#7](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/7): sitting, cinematic state and relog animation; use event-driven transitions with known terminal states.
- [#55](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/55), [#57](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/57), [#58](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/58), [#69](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/69): held items, lighting, equipment, model selection; require scene-aware appearance snapshots.
- [#59](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/59)-[#61](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/61), [#84](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/84): Snowfox, Seatruck, Prawn, SpyPenguin; separate vehicle ownership/seat logic from player avatar.
- [#62](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/62): multiplayer menu, hosting, joining and difficulty; ship a menu only after networking and session lifecycle are stable.
- [#8](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/8)-[#12](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/12), [#22](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/22)-[#51](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/51): broad construction/furniture completion; treat as feature coverage, not proof of current bugs.
- [#86](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/86)-[#100](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/100): item/tool implementations; model individual item behavior later, behind a synchronized action API.
- [#11](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/11), [#15](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/15), [#53](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/53): jukebox title, controls and time; shared playback clock/track ownership.
- [#14](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/14): Windows Firewall port discussion. Original author said it did not actually cause problems; do **not** silently alter firewalls. Use explicit documented opt-in instead.
- [#16](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/16): first-login lobby needs consistent readiness and player baseline.
- [#65](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/65), [#73](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/73): sounds and image-frame interaction; observer/event tests.
- [#67](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/67), [#68](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/68): console restrictions/game mode toggles; never unnecessarily modify vanilla singleplayer.
- [#54](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/54): construction performance; batch and deduplicate events.

## Policy for our regression suite

1. Keep network lifecycle state machine deterministic and bounded.
2. Test two simultaneous clients plus retry, disconnect, reconnect and cached first snapshot.
3. Test no duplicate ghost objects after retransmitted state or repeated disconnect events.
4. Test singleplayer/disabled-plugin mode as a strict no-op.
5. Add 300ms RTT, jitter, 2-10% loss, duplication/reordering and malformed frame tests.
6. Before world sync, define lease expiry, tombstones, revisioned entity identity and save migration.
7. Ensure CI tests succeed without game binaries; later add opt-in manual verification with legitimate BepInEx + Below Zero assemblies.

**Status:** first-round requirements audit, not a claim of reproducing or fixing the upstream bugs. Original maintainer marked many alpha items completed; the network-test checklist in #83 was closed as no longer necessary, not exhaustively checked.

## Follow-up implementation: world-state foundations

See [WORLD_STATE.md](WORLD_STATE.md). An independent, opt-in server-side
world-state engine now covers these historical regressions in standalone tests:
lease expiry/release (#77), destruction/rebuilding with cleared inventory and
persistent tombstones (#82), late-join snapshot + ordered change catch-up with
fallback when the bounded history is exhausted (#85). **This engine is not
attached to real Subnautica construction or inventory hooks yet.** An
authenticated event channel and gameplay authority checks remain prerequisites.

## Follow-up implementation: quickslots and power

See [METADATA_POWER.md](METADATA_POWER.md). The standalone server domain now
guards against silent slot loss during metadata migration (#78) and stale
generation after a producer is removed (#81). Passing tests do not claim
real Below Zero gameplay synchronization is complete.

## Follow-up implementation: Fabricator interaction and item delivery

See [FABRICATION.md](FABRICATION.md). The standalone state machine now separates
UI leases, running work and output ownership. Historical #3/#76 regression
tests cover cancel/reopen, repeated claims, disconnects, job completion and
crash recovery. It is not wired into the live Fabricator, game inventory or
the unauthenticated development UDP server.

## Additional fabrication safeguards

The follow-up implementation now adds trusted recipe ingredients, replay-safe
ingredient credits, refunds for unfinished cancelled/destroyed jobs, atomic
in-aggregate output item IDs and file-backed persistence-before-ack. See
[FABRICATION.md](FABRICATION.md). These are still independent-domain tests;
none of them changes the original mod or the installed Below Zero game.
