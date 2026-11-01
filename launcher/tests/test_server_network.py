"""Black-box integration tests for the unauthenticated local dev server."""
from pathlib import Path
import shutil
from concurrent.futures import ThreadPoolExecutor
from threading import Barrier
import socket
import struct
import subprocess
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / "src" / "BelowZeroCoop.Server" / "bin" / "Release" / "net8.0" / "BelowZeroCoop.Server.dll"
VERSION = 2
HELLO = 3
HELLO_ACK = 4
WELCOME = 6
PEER_LEFT = 7
GOODBYE = 8


def control(kind: int, value: int) -> bytes:
    return struct.pack("<4sBBI", b"BZC0", VERSION, kind, value)


def movement(seq: int, peer_id: int, timestamp: float, x: float,
             y: float = 0.0, z: float = 0.0) -> bytes:
    return struct.pack("<4sBBIIdfff", b"BZC0", VERSION, 5,
                       seq, peer_id, timestamp, x, y, z)


class ServerNetworkTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which("dotnet") or not SERVER.is_file():
            raise unittest.SkipTest("Build .NET 8 Release server first.")
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.bind(("127.0.0.1", 0))
            cls.port = probe.getsockname()[1]
        cls.process = subprocess.Popen(
            ["dotnet", str(SERVER), "--bind", "127.0.0.1", "--port", str(cls.port)],
            stdout=subprocess.DEVNULL, stderr=subprocess.PIPE
        )

    @classmethod
    def tearDownClass(cls):
        if hasattr(cls, "process"):
            cls.process.terminate()
            try:
                cls.process.communicate(timeout=3)
            except subprocess.TimeoutExpired:
                cls.process.kill()
                cls.process.communicate()

    def make_socket(self):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.connect(("127.0.0.1", self.port))
        sock.settimeout(0.2)
        return sock

    def receive_matching(self, sock, predicate, timeout=2.0):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                data = sock.recv(100)
            except (socket.timeout, ConnectionRefusedError):
                continue
            if predicate(data):
                return data
        self.fail("Did not receive expected datagram before timeout")

    def join(self, sock, seq: int) -> int:
        deadline = time.monotonic() + 5
        ack = False
        peer_id = None
        while time.monotonic() < deadline:
            sock.send(control(HELLO, seq))
            for _ in range(3):
                try:
                    data = sock.recv(100)
                except (socket.timeout, ConnectionRefusedError):
                    break
                if data == control(HELLO_ACK, seq):
                    ack = True
                elif len(data) == 10 and data[5] == WELCOME:
                    peer_id = struct.unpack("<4sBBI", data)[3]
                if ack and peer_id is not None:
                    self.assertGreater(peer_id, 0)
                    return peer_id
            if self.process.poll() is not None:
                self.fail("Development server terminated during handshake")
        self.fail("Server never finished Hello/Welcome handshake")

    @staticmethod
    def leave(sock):
        try:
            sock.send(control(GOODBYE, 99))
        except OSError:
            pass

    def test_concurrent_joins_have_unique_identities(self):
        # Historical upstream issue #63: two simultaneous joins could
        # prevent anyone from entering the session.
        count = 4
        barrier = Barrier(count)

        def enter(index):
            with self.make_socket() as connection:
                barrier.wait(timeout=5)
                peer_id = self.join(connection, 500 + index)
                self.leave(connection)
                return peer_id

        with ThreadPoolExecutor(max_workers=count) as pool:
            identities = list(pool.map(enter, range(count)))

        self.assertEqual(len(identities), count)
        self.assertEqual(len(set(identities)), count)

    def test_handshake_and_ping(self):
        with self.make_socket() as client:
            client.send(control(1, 11))
            with self.assertRaises((socket.timeout, ConnectionRefusedError)):
                client.recv(100)

            assigned = self.join(client, 12)
            self.assertGreater(assigned, 0)
            client.send(control(1, 13))
            self.assertEqual(
                self.receive_matching(client, lambda d: d == control(2, 13)),
                control(2, 13)
            )

            bad_version = bytearray(control(1, 14))
            bad_version[4] = 1
            client.send(bad_version)
            with self.assertRaises(socket.timeout):
                client.recv(100)

            with self.make_socket() as unregistered:
                unregistered.send(control(1, 15))
                with self.assertRaises(socket.timeout):
                    unregistered.recv(100)
            self.leave(client)

    def test_movement_relay_and_validation(self):
        with self.make_socket() as sender, self.make_socket() as receiver:
            sender_id = self.join(sender, 100)
            self.join(receiver, 101)

            sender.send(movement(1, 0, 1.0, 10.0))
            data = self.receive_matching(receiver, lambda d: len(d) == 34)
            _, version, kind, seq, peer_id, stamp, x, y, z = (
                struct.unpack("<4sBBIIdfff", data))
            self.assertEqual((version, kind, seq, peer_id), (VERSION, 5, 1, sender_id))
            self.assertEqual((stamp, x, y, z), (1.0, 10.0, 0.0, 0.0))

            for rejected in (
                movement(2, sender_id, 1.05, 10.5),
                movement(1, 0, 1.10, 11.0),
                movement(2, 0, 1.15, 5000.0),
            ):
                sender.send(rejected)
                with self.assertRaises(socket.timeout):
                    receiver.recv(100)

            time.sleep(0.06)
            sender.send(movement(2, 0, 1.20, 10.5))
            data = self.receive_matching(receiver, lambda d: len(d) == 34)
            _, _, _, seq, stamped_id, _, x, _, _ = (
                struct.unpack("<4sBBIIdfff", data))
            self.assertEqual((seq, stamped_id, x), (2, sender_id, 10.5))
            self.leave(sender)
            self.leave(receiver)

    def test_late_join_baseline(self):
        with self.make_socket() as source, self.make_socket() as newcomer:
            sender_id = self.join(source, 200)
            source.send(movement(9, 0, 50.0, 17.5))
            # Round-trip ping ensures the baseline has reached the server.
            source.send(control(1, 201))
            self.receive_matching(source, lambda d: d == control(2, 201))
            self.join(newcomer, 202)
            baseline = self.receive_matching(
                newcomer, lambda d: len(d) == 34)
            _, _, kind, sequence, identity, _, x, _, _ = (
                struct.unpack("<4sBBIIdfff", baseline))
            self.assertEqual((kind, sequence, identity, x), (5, 9, sender_id, 17.5))
            self.leave(source)
            self.leave(newcomer)

    def test_duplicate_hello_does_not_amplify_world_baseline(self):
        # Repeated Hello must still get Ack/Welcome, but must not force a
        # movement snapshot flood for every retry. A later retry can recover.
        with self.make_socket() as source, self.make_socket() as newcomer:
            source_id = self.join(source, 240)
            source.send(movement(1, 0, 10.0, 22.0))
            source.send(control(1, 241))
            self.receive_matching(source, lambda d: d == control(2, 241))

            newcomer_id = self.join(newcomer, 242)
            first = self.receive_matching(newcomer, lambda d: len(d) == 34)
            self.assertEqual(struct.unpack("<4sBBIIdfff", first)[4], source_id)

            newcomer.send(control(HELLO, 243))
            self.receive_matching(newcomer, lambda d: d == control(HELLO_ACK, 243))
            self.receive_matching(newcomer,
                                  lambda d: d == control(WELCOME, newcomer_id))
            with self.assertRaises(socket.timeout):
                newcomer.recv(100)

            # After the bounded retry interval, the sender can recover a
            # previously lost baseline without changing its server identity.
            time.sleep(2.05)
            newcomer.send(control(HELLO, 244))
            self.receive_matching(newcomer, lambda d: d == control(HELLO_ACK, 244))
            self.receive_matching(newcomer,
                                  lambda d: d == control(WELCOME, newcomer_id))
            resent = self.receive_matching(newcomer, lambda d: len(d) == 34)
            self.assertEqual(struct.unpack("<4sBBIIdfff", resent)[4], source_id)
            self.leave(source)
            self.leave(newcomer)

    def test_goodbye_notifies_and_rejects_old_endpoint(self):
        with self.make_socket() as source, self.make_socket() as observer:
            source_id = self.join(source, 300)
            self.join(observer, 301)
            self.leave(source)
            self.assertEqual(
                self.receive_matching(
                    observer, lambda d: d == control(PEER_LEFT, source_id)),
                control(PEER_LEFT, source_id)
            )
            source.send(movement(1, 0, 1.0, 99.0))
            with self.assertRaises(socket.timeout):
                observer.recv(100)
            self.leave(observer)

    def test_hello_retry_keeps_movement_ordering(self):
        with self.make_socket() as source, self.make_socket() as observer:
            source_id = self.join(source, 401)
            self.join(observer, 402)
            source.send(movement(10, 0, 1.0, 1.0))
            self.receive_matching(observer, lambda d: len(d) == 34)

            # Retried Hello should not re-enable replaying sequence 9.
            source.send(control(HELLO, 401))
            self.receive_matching(source, lambda d: d == control(HELLO_ACK, 401))
            self.receive_matching(source, lambda d: d == control(WELCOME, source_id))
            source.send(movement(9, 0, 1.1, 2.0))
            with self.assertRaises(socket.timeout):
                observer.recv(100)
            self.leave(source)
            self.leave(observer)

    def test_non_loopback_requires_explicit_opt_in(self):
        completed = subprocess.run(
            ["dotnet", str(SERVER), "--bind", "0.0.0.0", "--port", "27021"],
            capture_output=True, text=True, timeout=5
        )
        self.assertEqual(completed.returncode, 2)
        self.assertIn("--allow-lan", completed.stderr)


if __name__ == "__main__":
    unittest.main()
