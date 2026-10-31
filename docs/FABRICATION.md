# Fabricator lifecycle and item handoff — upstream #3 and #76

This module is an **independent, server-side development model** written for
BelowZeroCoop. It does not use the original mod's code, game assets or binaries.

Original reports:
- [#3 — Crafter / Fabricator / Workbench interaction conflict when receiving a completed item](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/3)
- [#76 — Crafter will not reopen after crafting and collecting an item](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/76)

The original issues were resolved by their maintainer years ago. These are
historical regression cases, not evidence of present-day defects.

## Independent model

`src/BelowZeroCoop.Server/FabricationState.cs` has an explicit state for
each job:

```
  Running -> Ready -> Delivering -> Claimed
      |                   |
      +-> Cancelled       +-> (retries with same item UUID)
```

The station's *UI lease* is independent of its crafting job and of the
output's delivery state. A running job blocks starting a competing job, but
once the result becomes Ready, a new player can open the Fabricator even
before the original owner collects the output. An uncollected output cannot
keep the station's interaction locked (#76).

The model also tracks:
- Stable station UUID and generation. A destroyed/rebuilt station must use a
  newer generation, so stale events from a previous machine are rejected.
- Server-defined recipe whitelist with fixed output type and time; clients
  cannot select arbitrary items just by sending their name.
- Time-bounded UI leases; `ReleaseActor` also clears abandoned leases.
- Bounded job and station counts; no unbounded memory allocation.
- Job UUID as the idempotency key. Repeating a Begin request returns the
  same output UUID; conflicting actor/recipe/station reuse is rejected.
- Owner-only output retrieval. A disconnect doesn't give another player the
  unfinished or uncollected item.
- Cancelled or destroyed works can never produce a completed result.
- Jobs and items are serializable to our own JSON format, independent from
  Subnautica game save files; ephemeral UI leases never survive a crash.

## Recoverable output handoff (#3)

A common source of dupes/lost items is treating an attempted pickup as final
before an inventory transaction is actually durable.

The model provides a **delivery outbox**:

1. `Claim(jobId, verifiedPlayerId, now)` transitions `Ready` to
   `Delivering` and returns a stable unique `OutputItemId`. This is only
   a delivery ticket, **not** a game inventory insert.
2. The future trusted gameplay adapter saves the pending outbox state before
   attempting the inventory insert.
3. The adapter inserts that exact instance UUID in the owner's inventory,
   using an idempotent item transaction, and durably saves the inventory.
4. Only after an inventory acknowledgement does the adapter invoke
   `ConfirmDelivery(jobId, verifiedPlayerId, outputItemId)`, then persist
   the confirmed fabrication state.
5. If a crash happens at any point, replay the same output UUID. The adapter
   must recognize a previously inserted item and acknowledge it, not insert
   a second copy.

A repeat `Claim` in `Delivering` returns the **same** output item ID with
`NewlyClaimed=false` and `Reason=pending_delivery`. A confirmed pickup
returns `Reason=already_claimed`. Confirmation is idempotent.

**Important integration limit:** the standalone model does not provide a
shared transactional database across game inventory and crafting state.
The future adapter must implement atomic or recoverable multi-store commits;
otherwise an incorrect save order can still lose or duplicate items. This
module does not make real game inventory guarantees on its own.

## Persistence and crash recovery

`GameplayStore.SaveFabrication` and `LoadFabrication` use a strict versioned
JSON document, atomic same-filesystem rename and previous-snapshot `.bak`.
Malformed documents, missing nested lifecycle fields, duplicate identities,
contradictory active jobs or unknown recipes are rejected on load.

Only the independent BelowZeroCoop format is supported. This is **not** a
migration of the original mod or the game's save data.

The code is not reachable from the current unauthenticated UDP development
listener. Real server authority must authenticate the player and station,
check recipe/material requirements, reserve ingredient consumption, and
manage refund rules before enabling crafting gameplay.

## Regression suite

```sh
dotnet run --project src/BelowZeroCoop.FabricationTests -c Release
```

Covers concurrent UI openings, repeated Begin/Cancel/Claim, interrupted
delivery and restart, reassigned station generations, disconnected owners,
lease expiry, source ownership checks, re-opening after collection, and
rejection of broken snapshots.

## Remaining before playable co-op

- Map trusted recipe IDs, output instances, materials, pickup animation and
  UI callbacks to verified Below Zero game code in a separate integration.
- Reserve/consume ingredients atomically; refund policy for cancelled work.
- Host-only gameplay authorization (network IDs are not authenticated).
- Durable idempotent transfer between FabricationState and EquipmentState.
- Handle station destruction, game scene unload and save migration.
- Capacity management of old claimed/cancelled jobs without forgetting
  idempotency keys; until then MaxJobs caps outstanding/history records.
- Validate Linux/Proton and Windows lifecycle using a legitimate game install.

**Status: testable standalone architecture, not a claim of a working Fabricator
multiplayer mod or a fix applied to the original game.**
