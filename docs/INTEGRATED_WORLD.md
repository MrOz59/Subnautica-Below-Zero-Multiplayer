# Integrated local world transaction (development-only)

This is a **single-host .NET 8 prototype**, NOT Subnautica's native save
format, NOT a network-reachable API and NOT yet a working multiplayer world.

See also [CONSTRUCTION.md](CONSTRUCTION.md),
[WORLD_STATE.md](WORLD_STATE.md), [METADATA_POWER.md](METADATA_POWER.md),
[FABRICATION.md](FABRICATION.md), and the
[original alpha issue audit](UPSTREAM_ISSUES_1-100.md).

## Why this exists

Historical issues #6, #10, #12, #77, #79, #80, #81, #82 and #85 overlap:
destroying a room can invalidate a power source, its cables, the Fabricator
inside it, inventory/material reservations and the world entity index.

Saving these separately can produce half-destroyed bases after a crash.
The previous individual models and test suites remain intact. This is an
**additional coordinated mode** that uses a single transaction boundary.

## One composite snapshot

`src/BelowZeroCoop.Server/IntegratedWorldSession.cs` owns a snapshot
containing four independently versioned domains:

1. `ConstructionState`: entity geometry, structural support, detachable
   power wiring, generations, tombstones and ordered topology changes.
2. `WorldState`: the matching entity identity and tombstone index, including
   global revision and cached entity changes.
3. `PowerGridState`: source/consumer identities, generation, stored energy,
   integrated production/consumption and power configuration revisions.
4. `FabricationState`: constructed station identity, jobs, reservations,
   output receipts and the internal item/material ledger.

A successful operation clones the current aggregate, applies all changes
to the clones, checks cross-domain invariants, flushes a temporary JSON
snapshot and replaces the primary file **before** publishing the new
in-memory state or returning a successful new commit.

The exclusive `.lock` file prevents two local writers from opening the same
world. A previous complete primary is retained in `.bak`; recovering from
corruption is intentionally an explicit operator action, not an automatic
reset. It does not modify Below Zero saves.

## Coordinated operations

### Place / rebuild

`Place` adds the construction node and **the same entity ID, kind, generation
and live/tombstoned status** in `WorldState`. Placing a Fabricator registers a
corresponding Fabricator domain station. A failed station registration or
world-index update rejects the *entire* proposed operation.

`InstallPowerDevice` accepts only a live matching construction generation
and a role compatible with this prototype's whitelist (SolarPanel,
NuclearReactor, Bioreactor generators; Fabricator/WaterFiltration consumers).
These labels are **provisional developer examples**, not a claim that they map
to actual game prefabs or rates.

### Remove / cascade

A trusted host calls `Remove` with the expected construction revision, a
fresh UUID, current occupied-player bounds and a host monotonic clock value.

- Validates the complete removal plan; structural descendants require
  explicit cascading, and player occupancy blocks the entire removal.
- Writes construction tombstones, removes structural/power links, and creates
  **one atomic topology change** containing all affected IDs and wires.
- Advances power simulation up to the trusted removal timestamp, then
  tombstones power devices belonging to removed nodes.
- Cancels unfinished jobs of deleted Fabricators and refunds their reserved
  materials exactly once; previously completed results remain reserved.
- Tombstones the same entities in `WorldState` using internal host authority.
- Validates all four resulting domains and stores them together or publishes
  **nothing**.

A replay of the same removal UUID does not duplicate refunds, delete a newly
rebuilt structure or advance energy again.

### Crafting

`GrantMaterials`, `BeginCraft`, `CancelCraft` and `DeliverCraft`
exercise the Fabricator's existing internal material ledger inside the
composite document. `BeginCraft` obtains an ephemeral interaction lease on
the tentative clone; this is not a full in-game crafting UI protocol.
Output UUIDs remain stable across restarts and retries.

## Cross-domain validation

Loading and saving fail closed when a live PowerGrid device has no matching
construction node/generation or a Fabricator station disagrees with the
construction graph. Every construction node must have a matching WorldState
entity with the same UUID, generation, kind and deleted flag. A live
Fabricator construction must have a live station. The loader also rejects
missing required nested fields instead of defaulting stored power to zero.

No client can invoke these methods through the existing UDP debug server.

## Tests

```sh
dotnet run --project src/BelowZeroCoop.WorldTests -c Release
```

`IntegratedWorldTests` simulates:
- A room, power source, power wiring and active Fabricator with reserved inputs.
- Failed noncascade or occupied removal preserving all states.
- Successful atomic cascade, energy settlement and job/material cleanup.
- Replayed removal, reconstructing a Fabricator with a newer generation.
- Restart recovery of all four domains and a uniquely delivered craft output.
- Invalid power clock and filesystem write errors leaving the prior snapshot.
- Corrupt/orphan power devices, mismatching world IDs and missing power fields.
- Concurrent Connect/Remove operations with one winning revision.

This test suite uses deterministic domain objects and local JSON, not Unity or
gameplay integration, and does **not** prove real game/Proton compatibility.

## Known gaps

- The present model uses an independent trusted-host actor for `WorldState`
  mirroring; no authenticated online identity has been implemented.
- Device energy source rates must be settled before changing a live rate.
  `Remove` performs this step, but arbitrary live source reconfiguration
  still requires a dedicated host-timed update operation.
- `Remove` advances crafting to its trusted host UTC timestamp before teardown.
  Completed output stays claimable; only still-running jobs are cancelled and
  refunded. The future game adapter must supply an actual authoritative game
  clock, never an untrusted client timestamp.
- The Fabricator internal inventory is distinct from `EquipmentState` and
  the real game player inventory. Production and world integration are not
  complete until these are reconciled transactionally.
- Physical base shape, structural parenthood and containment still use test
  volumes and **must** be resolved from verified game APIs.
- The power topology links do not yet drive real energy distribution; source
  identity and removal are coordinated, but a full power network solver is
  future work.
- Save-after-every-operation is expensive and has no transactional game-save
  adapter, durable append-only journal, replicated log or cross-machine
  leader election. It's a deliberate development safety boundary.
- No real BepInEx/Unity assembly validation, physical-player occupancy
  callbacks, reliable gameplay events or Windows/Linux Proton multiplayer
  playtest has been completed.
