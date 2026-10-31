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
- [ ] Validate build against the exact installed Below Zero version.
- [ ] Test on real Linux/Proton and Windows systems.

## M1 - Local playable prototype

- [ ] Versioned authenticated handshake and connection UI.
- [ ] Replace debug capsules with validated real character models and animation.
- [ ] Add interest management, area-based visibility, and spawn/despawn events.
- [ ] Host-validated player state, disconnect and reconnect.
- [ ] LAN two-player swim test (both Windows and Proton).
- [ ] Simulate high-latency 300 ms network conditions.

## M2 - World and vehicles

- [ ] Entity identity and world object ownership.
- [ ] Creature synchronization and server ownership.
- [ ] Seatruck / Prawn / Snowfox state and transitions.
- [ ] World save / deterministic reconciliation.
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
