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
- [x] Transactional plugin install, rollback and idempotency tests.\n- [x] Standalone jitter-adaptive movement buffer and synthetic 300 ms RTT tests.
- [ ] Validate build against the exact installed Below Zero version.
- [ ] Test on real Linux/Proton and Windows systems.

## M1 - Local playable prototype

- [ ] Versioned authenticated handshake and connection UI.
- [ ] Serialize local player snapshots; spawn remote player visuals.
- [ ] Connect the standalone jitter-adaptive interpolation buffer to remote players and add interest management.
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
