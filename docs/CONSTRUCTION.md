# Construction topology, dependent removal and power links

Standalone regression guard for historical upstream issues:

- [#79 — GlassDome could not be removed after installing a nuclear reactor](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/79)
- [#80 — PowerTransmitter sometimes prevented uninstalling other constructions](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/80)
- [#6 — null exception during construction removal](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/6)
- [#10 — do not destroy an occupied completed base](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/10)
- [#12 — construction could be placed on top of a player](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/12)

These issues were historically marked fixed by the original author; this is
independent defensive development, NOT code copied from the original mod,
and NOT proof of an existing bug in a current game installation.

## Topology model

`src/BelowZeroCoop.Server/ConstructionState.cs` models bounded construction
entities with UUIDs, generation, revision, type, occupancy volume, tombstone
status and explicit connections.

Two different link classes are intentionally separate:

- **Structural**: directed from a supported child to its supporting parent.
  A child such as an attached reactor must be detached/removed before an
  ordinary removal of its parent (e.g., a glass dome).
- **Power**: a detachable connection; it never by itself blocks destruction
  of a room, dome, transmitter or independent structure. Deleting either
  endpoint removes only the affected power connections, not the other endpoint.

Structural dependency cycles, duplicate links, references to deleted nodes
and references to an old generation are rejected. A power transmitter that
becomes disconnected cannot retain an invisible stale edge in this model.

This distinction matters for issue #80, which included a follow-up report
that power transmitters could even prevent removal of *other* constructions.

## Safe removal and occupancy

`PreviewRemove` plans a removal for the current global revision. It reports
structural dependents and affected links without mutating state.

`Remove` rechecks the entire plan under one lock. That prevents a client from
removing a room based on a preview made *before* another player attached
something to it.

- Normal removal rejects live structural children.
- Explicit `cascade=true` removes at most 128 structural dependents together,
  logically child-first, while detaching any attached power edges.
- Every affected element is checked against trusted occupied volumes. If a
  player is in *any* volume, **nothing is removed**.
- Failed operations leave the global revision, entities and edges untouched.
- Committed operations are idempotent via operation UUID and revision.
- Tombstones persist after restart; rebuilding an entity requires exactly the
  next generation so stale detach/remove events cannot destroy the new part.

The geometric bounds are **proxy volumes only**. The real adapter will need
game-specific collider data for rooms, base interiors, vehicles and players
rather than trusting a client-supplied occupancy list.

## Replication, recovery and persistence

One successful topology command produces one new global revision. A single
`ConstructionChange` can include both deleted nodes and removed links.

The bounded `TryChangesSince(revision)` stream preserves these grouped
changes for future replay to late joiners; when too old it returns false and
requires a new full snapshot. Neither snapshot nor delta commits reach the
Unity game yet.

`GameplayStore.SaveConstruction` / `LoadConstruction` persist this standalone
graph as JSON schema v2, using a temporary file, an explicit `.bak` previous
snapshot and strict restore checks. Incomplete or malformed files are rejected
rather than loaded as an empty world.

The graph is **currently separate** from the earlier `WorldState` entities,
`PowerGridState` energy model and `FabricationState` jobs. In-game construction
integration must use one authoritative transaction boundary so removing a
power transmitter also updates the *actual* power network, and destroying a
Fabricator also reconciles the *actual* crafting jobs. This integration is
not implemented or claimed.

The unauthenticated development UDP listener does **not** expose any
construction-changing endpoint.

## Regression tests

```sh
dotnet run --project src/BelowZeroCoop.WorldTests -c Release
```

The construction suite covers: power transmitters and unrelated room removal,
real structural dependency blockers, optional atomic cascade, occupancy guard,
player-overlap placement rejection, entity generation after deletion/rebuild,
malformed references, stale preview races, concurrent link/remove conflict,
repeated operation IDs, save/restore, corrupted snapshots, and atomic topology
delta delivery.

## Remaining before playable construction sync

1. Ground structural/power link semantics and occupied volumes in verified
   Subnautica: Below Zero game APIs (using legitimate local game assemblies).
2. Integrate topology IDs with `WorldState` and the world persistence journal.
3. Unify structural removal with power, crafting and inventory transactions.
4. Implement an authenticated reliable ordered replication channel and a
   staging/reconciliation process for late joins.
5. Test actual construction/teardown under concurrent clients on Windows and
   Linux/Proton, including vehicle occupancy and partial base interiors.

**Status: isolated design and CI regressions, not working multiplayer
construction inside the game.**
