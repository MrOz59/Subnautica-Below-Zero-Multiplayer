# Development UDP hardening and limitations

This is an independent, unauthenticated **LAN-only development preview**.
It is not a production multiplayer protocol and must not be Internet-exposed.
No real Below Zero world operations, game saves, inventory, or authorized
teleports pass through this UDP listener.

## Defensive boundaries

- **Source identity:** the server assigns a temporary peer number. The
  endpoint is the lookup key, not a peer ID claimed by an inbound movement
  packet. This prevents accidental peer-ID confusion but is **not real
  authentication**, encryption, or protection against spoofed UDP traffic.
- **Packet format:** control and movement messages have distinct fixed
  sizes and a common explicit wire version. Invalid sizes, types, numeric
  coordinates, timestamps and sequence numbers are rejected.
- **Movement ordering:** a client's send clock is used only for monotonic
  ordering. It cannot grant additional travel distance. The actual elapsed
  time between accepted messages at the server controls the speed check;
  large gaps are capped at 0.5 seconds, and a small positional tolerance is
  retained for numeric jitter. A legitimate in-game teleport will require
  a separately authenticated event in a future protocol revision.
- **Idle expiry:** malformed/replayed movement never refreshes the endpoint
  session. Periodic housekeeping disconnects and notifies surviving peers.
- **Handshake retries:** repeated Hello requests still receive HelloAck and
  Welcome. The expensive snapshot replay is restricted to one baseline per
  endpoint every two seconds. A later retry can recover a lost baseline,
  without resetting accepted movement sequence numbers.
- **Client preview:** remote visual objects are disposable debug capsules,
  not real player avatars. Cleanup on PeerLeft or expiry remains explicit.

## Relevant regression commands

```sh
dotnet build src/BelowZeroCoop.Server -c Release
dotnet run --project src/BelowZeroCoop.PeerTests -c Release
dotnet run --project src/BelowZeroCoop.SmokeTests -c Release
python3 -m unittest discover -s launcher/tests -p test_server_network.py -v
```

The network integration suite runs real UDP loopback sockets. The peer tests
simulate elapsed time, including malformed direct calls into the registry.
The smoke suite covers deterministic malformed datagrams and packet
loss, jitter, duplication and reordering, based on upstream issue #83.

## Required before real co-op or remote access

Use cryptographic session authentication, replay protection, explicit
server-authoritative gameplay transitions, transport-level rate limiting,
admission control, a reliable ordered event channel, player spawn/readiness
negotiation and game-version compatibility checks. Do not advertise the
current debug UDP server as secure or multiplayer-ready.

The original BOT Benson project is used for historical issues and public
game API naming research only; no original patch bodies or protocol formats
were transplanted.
