# Metadata and power accounting — historical regressions #78 and #81

This is an **independent development-only design** in BelowZeroCoop. None of
these classes is connected to the real Subnautica: Below Zero quickslot UI,
energy graph, native saves, game entity hooks, or public UDP transport.

The models provide an isolated, versioned reference implementation and test
bed for requirements learned from the original project's reports:
- [#78: metadata changes broke quickslots](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/78)
- [#81: removed nuclear generation sometimes remained in total power](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/81)

Do not interpret passing tests as confirmation that either historical bug
exists today or has been corrected inside the game.

## 1. Player equipment and quickslots (#78)

Location: `src/BelowZeroCoop.Server/EquipmentState.cs`

- Each owned inventory item instance has a unique UUID. Slot bindings refer to
  this UUID, not a display name, array position or mutable type label.
- Five slots are modeled in the standalone prototype; that must be checked
  against the actual game client before game integration.
- Items can be added, removed, bound, moved to another slot or cleared.
- An item cannot be equipped unless it is currently owned.
- Moving the same instance to another slot clears its previous binding
  atomically, avoiding duplicate references.
- Removing an item automatically clears its slot.
- Commands carry an operation UUID and expected per-player revision.
  Replays are idempotent. A repeated UUID with changed content is rejected.
- An explicit caller-provided verified player identity must match the
  requested profile. The future game adapter MUST bind that identity to
  a real authenticated game session; it must not trust UDP-provided IDs.
- Profiles, operation receipts and slot bindings are serializable as schema
  version 2.
- A deliberately small `MigrateLegacy` function converts **our own**
  version-1 prototype representation. It rejects unresolved item references
  instead of silently dropping missing quickslots.
- This is **not** a migration for the BOT Benson mod or native game metadata.

The deserializer rejects missing nested `OwnedItems` or `Slots`, dangling
references, duplicate item ownership, duplicate slot bindings and malformed
revisions. Invalid files must never be treated as an empty loadout.

## 2. Power source and consumer graph (#81)

Location: `src/BelowZeroCoop.Server/PowerGridState.cs`

- Generator and consumer identities are UUIDs, with explicit generations.
- Removing a generator leaves a tombstone; an old replay cannot resurrect it.
  A rebuilt device must advance its generation.
- Power configuration changes are revision-checked and idempotent.
- Energy stored, power rates, production, consumption and unmet demand are
  tracked in **integer milli-units**, not floating point.
- Production and consumption integrate over bounded monotonic millisecond
  ticks; fractional remainders carry forward without cumulative drift.
- Demand reduces stored energy. Once depleted, unmet demand is tracked
  explicitly instead of allowing negative storage.
- Excess generated energy beyond storage capacity is tracked as overflow.
- Untrusted huge time jumps, negative times, invalid rates and oversized
  energy capacity are rejected.
- A saved grid may explicitly rebase the monotonic timer **once before its
  first new tick**. This does not credit offline generator output.
- The real game's producer/consumer rates, batteries, transmission networks,
  charging mechanics and scaling are **not yet measured or integrated**.

Important: the future game adapter must process time up to a change BEFORE
applying an updated device rate. A zero-rate/remove event can otherwise
incorrectly affect already elapsed time. Do not expose these operations on the
current unauthenticated UDP development listener.

## 3. Persistence

Location: `src/BelowZeroCoop.Server/GameplayStore.cs`

- Explicit `SaveEquipment/LoadEquipment` and `SavePower/LoadPower` APIs.
- JSON snapshots are versioned, size-limited and schema-validated.
- Save writes to a temporary file in the same directory, flushes it, backs up
  the previous complete snapshot to `.bak`, then atomically renames the new
  file into place on the same filesystem.
- Invalid JSON, omitted nested fields, dangling item IDs and inconsistent
  energy records are rejected, **never silently reset**.
- Backups are **manual recovery files**, not an automatic repair mechanism.
- The code never edits the game's native save files or the original mod files.
- There is no append-only transactional journal yet; changes made after the
  last explicit save are not guaranteed durable after a power failure.

## 4. CI regressions

Run from repository root:

```sh
dotnet run --project src/BelowZeroCoop.MetadataTests -c Release
```

The tests verify:
- Stable unique item identities and correct quickslot bindings.
- Prevention of dangling, duplicate or unowned slot references.
- Stale-revision and changed-payload operation rejection.
- Explicit schema migration with no silent data loss.
- JSON disk roundtrips, backups and malformed/missing fields.
- Removing and rebuilding power sources with no phantom production.
- Production + demand accounting, fractional integration and storage bounds.
- Simulated server restart, timer rebase and no offline power gain.

## Next integration gates

1. Check exact game assemblies and player/inventory/power APIs on Windows
   and Linux/Proton using legitimately installed game binaries.
2. Build a reliable ordered, authenticated gameplay event channel. Keep
   the existing public development UDP transport unable to mutate world state.
3. Bind metadata events to stable player and item instance identity.
4. Bind each power source/consumer to a stable world entity and generation.
5. Reconcile game save and our authoritative snapshot transactionally.
6. Add deterministic multi-client join, disconnect, restore and stale-replay
   scenarios before claiming either historical issue resolved in-game.
