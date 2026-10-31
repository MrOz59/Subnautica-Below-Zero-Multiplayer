# Roadmap

The goal is a new project supporting Linux through Steam/Proton as a
first-class runtime, while remaining compatible with Windows.

## M0 - Independent foundation (this branch)

- [x] Fresh tree, own launcher and own code.
- [x] Steam installation discovery, diagnostics and Proton notes.
- [x] Local BepInEx plugin install.
- [x] BepInEx plugin entry point and test probe.
- [x] Network protocol + local UDP test server + smoke tests.
- [x] Versioned Hello/HelloAck and expiring endpoint sessions.
- [x] UDP integration test against the running server.
- [x] Transactional plugin install, rollback and idempotency tests.
- [x] Standalone jitter-adaptive movement buffer and synthetic 300 ms RTT tests.
- [x] Development-only position codec, peer relay, speed cap and basic replay protection.
- [x] Optional 20 Hz Unity position publisher and remote debug capsules.
- [x] Explicit LAN opt-in in the launcher, with persisted server logs.
- [x] Server-assigned peer identities and late-join movement baseline.
- [x] Best-effort Goodbye, PeerLeft and idle peer expiration.
- [x] Repeated Hello retains anti-replay movement sequence state.
- [x] Historical issue audit (#1-100): docs/UPSTREAM_ISSUES_1-100.md.
- [x] CI regressions: concurrent joins (#63), duplicate remote capsules (#5),
      singleplayer disabled-network no-op (#64), packet impairments (#83).
- [ ] Validate build against the exact installed Below Zero version.
- [ ] Test on real Linux/Proton and Windows systems.

## M1 - Local playable prototype

- [ ] Versioned authenticated handshake and connection UI.
- [ ] Replace debug capsules with validated real character models and animation.
- [ ] Add interest management, area-based visibility, and spawn/despawn events.
- [ ] Host-validated player state, disconnect and reconnect.
- [ ] LAN two-player swim test (both Windows and Proton).
- [x] Deterministic 300 ms RTT simulation with 10% loss, jitter,
      duplicate and reordered movement snapshots.
- [ ] Real network-emulator tests and a complete 300 ms RTT gameplay test.

## M2 - World and vehicles

- [x] Standalone revisioned entity IDs, generations, ephemeral ownership leases
      and idempotent commands (no game integration yet).
- [x] Bounded incremental world-state changes and snapshot fallback for joins.
- [x] JSON persistence and explicit previous-snapshot backup.
- [x] Regressions for upstream #77, #82, #85, including restart recovery.
- [ ] Integrate world events with authenticated game sessions and host checks.
- [x] Independent, versioned quickslot metadata with migration and persistence,
      covered by upstream #78 regressions (not yet in the Unity game).
- [x] Deterministic integer base power accounting, source generations and
      persistent snapshots, covered by upstream #81 regressions.
- [x] Standalone Fabricator job lifecycle, UI leases, durable output
      tickets and idempotent delivery acknowledgement (#3/#76).
- [x] Fabrication regressions for concurrent use, disconnects, reopens,
      destroyed stations, missing snapshots and pending-delivery restart.
- [ ] Authenticate player crafting requests and validate recipe, ingredient
      availability and atomic inventory consumption before any game integration.
- [x] Standalone ingredient reservation, single-aggregate output delivery and
      durable file-backed transaction facade with rollback on write failure.
- [x] CI verifies material replay, cancellation refunds, concurrent spending,
      output identity after restart and single-writer save locking.
- [ ] Unite the internal craft inventory with the actual player inventory and
      EquipmentState; enforce cross-system transactional reconciliation.
- [ ] Bind these systems to authenticated game entities and the game save.
- [ ] Creature synchronization and server ownership.
- [ ] Seatruck / Prawn / Snowfox state and transitions.
- [ ] Game-specific world save integration, migration and deterministic reconciliation.
- [ ] Checkpoints and compatibility migration.

## M3 - Production and launchers

- [ ] Release bundles from CI (our binaries only).
- [ ] Native packaged launcher for Linux (.AppImage / Flatpak).
- [ ] Native Windows packaged launcher.
- [ ] Steam Deck UX and Flatpak game libraries.
- [ ] Automatic update checking with signed releases.
- [ ] Epic Games support after testing legitimate installations.
- [ ] Authentication, rate limiting, invite codes, discoverability.
- [ ] Full in-game multiplayer settings and diagnostics.

Each milestone must remain independently testable. Do not claim online
playability until gameplay replication is implemented and tested.

- [x] Independent construction topology for historical #79/#80: structural
      dependencies versus detachable PowerTransmitter connections.
- [x] Safe construction removal previews, global revision checks, occupant
      guard, explicit atomic cascade, tombstones and persistent snapshots.
- [x] Bounded atomic topology changes for future late-join replication;
      stale revisions force a full resnapshot.
- [ ] Unify construction graph with WorldState, FabricationState and
      PowerGridState under one durable host-authoritative transaction.
- [ ] Validate actual game construction geometry and player occupancy hooks.

## Integration foundation (independent development)

- [x] Single-file transactional aggregate for construction topology, world
      entity index, power state and Fabricator jobs/ingredients.
- [x] Cross-domain identity/generation/tombstone validation during load/save,
      with fail-closed incomplete nested JSON checks.
- [x] Atomic cascade cleanup and preservation of finished versus unfinished
      Fabricator output, including replay and disk restart regressions.
- [ ] Replace provisional kind labels, geometry and host clocks with verified
      Below Zero gameplay APIs.
- [ ] Integrate with authenticated reliable multiplayer events and the
      game's own save/inventory system.

## Game API hook research

- [x] Audit original mod's public Harmony declarations for construction,
      Fabricator, power, inventory, quickslots, streaming and vehicle events.
- [x] Independent, opt-in read-only `Assembly-CSharp` method scanner
      with overload/missing-method reporting and fake-Unity CI checks.
- [ ] Confirm real game method signatures and coroutine semantics against
      the user's legitimately installed Below Zero assemblies.
- [x] Add independent off-by-default Harmony Postfix observers for the two
      candidate build entrypoints, with bounded logs and safe self-unpatch.
- [ ] Verify these candidate hooks against genuine game/BepInEx assemblies,
      including actual return values, overloads and coroutine behavior.
- [ ] Connect authenticated, host-authoritative observations to the
      integrated world transaction layer.

## Candidate identity extraction

- [x] Independent, off-by-default reflection snapshot for game
      UniqueIdentifier.Id, techType, constructedAmount and world/local
      position/rotation at observed method return.
- [x] Separate temporary BaseGhost, constructed-piece candidate and parent
      base IDs; reject absent/malformed IDs without inventing persistent IDs.
- [x] CI covers partial/completed candidates, transforms, nonfinite data
      and parent component lookup.
- [ ] Validate actual game signatures, UID semantics and final base-grid
      placement against legitimate Assembly-CSharp/Unity binaries.
- [ ] Establish provenance: local player build versus save loading,
      scripting and application of remote network events.
- [ ] Create authenticated gameplay events only after those validations;
      no debug observer records may mutate authoritative state.

## Verified-game-ID transaction boundary

- [x] Introduce append-only, deterministic, world-scoped game string
      ID to internal GUID mapping (not a volatile Unity hash).
- [x] Persist map in integrated schema v3 with fail-closed integrity
      checks and in-memory migration from prior schema v2.
- [x] Add host-only `PlaceObserved` with explicit provenance gate,
      transactional ID allocation and construction/world updates.
- [x] CI covers duplicate IDs, world scope isolation, capacity, tampered
      mappings, restart/rebuild, failed save and rejected unverified origin.
- [ ] Replace the development world scope with the actual verified save ID,
      and detect genuine source/provenance from game hooks.
- [ ] Integrate authenticated events; passive observations MUST NOT
      call `PlaceObserved` directly.
