# Shared-world state foundation (development only)

This is an **independent** implementation informed by failure cases described in
[the upstream #1-100 audit](UPSTREAM_ISSUES_1-100.md). The code lives in
`src/BelowZeroCoop.Server/WorldState.cs` and `WorldStore.cs`. It does not
copy the original multiplayer source or change the game's save format.

**This code is not connected to the game's build/crafter hooks or public UDP
protocol yet.** The current dev UDP service is unauthenticated and must never
accept world-modifying commands until a secure session binding is implemented.

## Entity identity and versioning — upstream #82

Each buildable has a stable UUID and a **generation**. Its global monotonic
`Revision` changes on every accepted operation.

- Initial creation: generation 1, expected revision 0, previously unseen UUID.
- Destroy: write a tombstone and clear stored inventory. A missing object is
  not silently recreated when a stale datagram arrives.
- Rebuild under the same UUID: generation must increase by exactly one, and
  command must reference the exact prior tombstone revision.
- An old inventory/destroy operation from an earlier generation is rejected.
- Object types and inventory keys are bounded and validated.

This prevents a removed bioreactor from carrying its previous fish into a new
construction, even when unreliable packets arrive out of order.

## Leases and logout — upstream #77

Interaction ownership is a *lease*, not a permanent reservation.

- `TryAcquireLease(entityId, actorId, now, ttl)`: only for live entities,
  duration greater than zero and no longer than 60 seconds.
- The same actor can renew it. Others cannot acquire it until expiry/release.
- `ReleaseActor(actorId)` removes all their leases at logout.
- `ExpireLeases(now)` removes expired leases independently of packet traffic.
- Leases are **never persisted**. A crashed host cannot leave players locked
  out of furniture on restart.

Future real-game integration must bind `actorId` to an authenticated session;
the current UDP peer ID alone is **not** an authorization credential.

## Idempotency and conflicts

- Each operation has a UUID. A replay with identical content is recognized
  and returns the original committed revision without changing world state.
- Reusing the same operation ID for different content is rejected.
- Every mutation must match the object's generation and revision.
- Mutating an object requires a valid lease held by the authenticated actor
  (once authentication is implemented).
- Invalid quantities, unknown object keys and oversized inventory are rejected.
- A bounded receipt cache (4,096 operations) is part of the persisted snapshot.
  If an old receipt has been evicted, normal revision/generation checks still
  reject stale state; the client must reconcile rather than assume success.

## Late joining and recovery — upstream #85

`Snapshot()` returns a detached, versioned set of all entities, including
tombstones. `TryChangesSince(revision)` returns a detached, ordered stream of
changes after a baseline snapshot revision.

The history window contains at most 2,048 changes. If the request is too old,
the method returns **false** and the caller must fetch a *new full snapshot*.
It never silently skips a gap. Joining while others build would use:

1. Acquire the authoritative snapshot at revision R.
2. Deserialize and apply that snapshot to a local staging scene.
3. Request changes after R, validate contiguous revisions, then apply in order.
4. If the change history was truncated, discard staging state and repeat from
   a new snapshot.
5. Mark the world ready only after baseline and catch-up complete.

This workflow is documented but **not yet wired into the Unity game scene**.

## Local persistence

`WorldStore.Save(path, state)` serializes our own `WorldDocument` schema
version 2 to a temporary file, flushes it, retains a `.bak` copy of the
previous complete snapshot, then renames the temporary file into place.

`WorldStore.Load(path)` validates schema and entity invariants. A corrupt,
incomplete, unsupported or oversized file produces an explicit error instead
of silently resetting constructions. Recovery from `.bak` is **manual** and
explicit; there is no automatic overwrite of a broken save.

This mechanism is cross-platform (.NET 8 on Linux and Windows). It never
reads, rewrites or restores Subnautica's native save directories.

## Current limits / before gameplay integration

- Snapshot storage only: no append-only durable event journal or WAL yet.
  Power loss before an explicit Save can lose changes made since the last Save.
- Leases, entities and revisions exist in the independent server domain model
  but are not connected to in-game player events or savepoints.
- No authorization, authenticated ownership, trusted movement/world authority,
  world chunk streaming, migration of native game saves or conflict-free offline
  editing.
- Do not accept WorldCommand through raw UDP. First implement authentication,
  reliable ordered events, payload limits, authority checks, transactional
  persistence and an explicit singleplayer isolation layer.
- A 2,048-entry delta window is a bounded cache, not an indefinite durable
  event history.
- Object identity and generation correctness are verified in a standalone
  test project; real game lifecycle and construction prefabs remain untested.

## Regression tests

`dotnet run --project src/BelowZeroCoop.WorldTests -c Release`

Covers lock ownership expiration/logout, stale writes, idempotent inventory
changes, tombstones and new generations, snapshot isolation/reload/backups,
late-join delta catch-up and history exhaustion, and concurrent object creation.

Related reports:
[upstream #77](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/77),
[upstream #82](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/82),
[upstream #85](https://github.com/ismail0234/Subnautica-Below-Zero-Multiplayer/issues/85).
