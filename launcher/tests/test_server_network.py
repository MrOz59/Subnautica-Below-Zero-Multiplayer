"""Black-box UDP integration tests for the development server (requires dotnet)."""
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
SERVER = ROOT / "src" / "BelowZeroCoop.Server" / "bin" / "Release" / "net8.0" / "BelowZeroCoop.Server.dll"

def packet(packet_type: int, sequence: int) -> bytes:
    return struct.pack("<4sBBI", b"BZC0", 1, packet_type, sequence)

class ServerNetworkTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not shutil.which("dotnet") or not SERVER.is_file():
            raise unittest.SkipTest("Build .NET 8 release server before running UDP integration tests.")
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


    def test_movement_relay_and_server_authority(self):
        def movement(seq, peer_id, timestamp, x, y=0.0, z=0.0):
            return struct.pack("<4sBBIIdfff", b"BZC0", 1, 5,
                               seq, peer_id, timestamp, x, y, z)

        def connect_peer(sock, hello_seq):
            sock.connect(("127.0.0.1", self.port))
            sock.settimeout(0.2)
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline:
                sock.send(packet(3, hello_seq))
                try:
                    if sock.recv(100) == packet(4, hello_seq):
                        return
                except (socket.timeout, ConnectionRefusedError):
                    pass
            self.fail("Could not establish development peer session")

        with (socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender,
              socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as receiver):
            connect_peer(sender, 100)
            connect_peer(receiver, 101)
            receiver.settimeout(0.5)
            sender.send(movement(1, 0, 1.0, 10.0))
            received = receiver.recv(100)
            self.assertEqual(len(received), 34)
            header, version, ptype, seq, peer_id, timestamp, x, y, z = (
                struct.unpack("<4sBBIIdfff", received))
            self.assertEqual((header, version, ptype, seq), (b"BZC0", 1, 5, 1))
            self.assertGreater(peer_id, 0)
            self.assertEqual((timestamp, x, y, z), (1.0, 10.0, 0.0, 0.0))

            # A client cannot impersonate another peer by choosing an ID.
            sender.send(movement(2, peer_id, 1.05, 10.5))
            with self.assertRaises(socket.timeout):
                receiver.recv(100)

            # A replay of an old sequence must not be relayed.
            sender.send(movement(1, 0, 1.10, 11.0))
            with self.assertRaises(socket.timeout):
                receiver.recv(100)

            # Large implausible speed must not be relayed.
            sender.send(movement(2, 0, 1.15, 5000))
            with self.assertRaises(socket.timeout):
                receiver.recv(100)

            time.sleep(0.06)
            sender.send(movement(2, 0, 1.20, 10.5))
            received = receiver.recv(100)
            _, _, _, seq, peer_id2, _, x, _, _ = struct.unpack(
                "<4sBBIIdfff", received)
            self.assertEqual((seq, peer_id2, x), (2, peer_id, 10.5))

    def test_handshake_and_ping(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.connect(("127.0.0.1", self.port))
            sock.settimeout(0.2)

            # A Ping without a successful Hello must not be answered.
            deadline = time.monotonic() + 5
            while True:
                sock.send(packet(1, 11))
                try:
                    sock.recv(100)
                    self.fail("Unauthenticated probe responded to ping")
                except (socket.timeout, ConnectionRefusedError):
                    pass

                # The .NET process may not have bound its socket yet.
                if self.process.poll() is not None:
                    self.fail("Server exited before it could complete a handshake")

                sock.send(packet(3, 12))  # Hello
                try:
                    data = sock.recv(100)
                    if data == packet(4, 12):  # HelloAck
                        break
                except (socket.timeout, ConnectionRefusedError):
                    pass

                if time.monotonic() >= deadline:
                    self.fail("Server did not respond to hello within five seconds")
                time.sleep(0.1)

            sock.send(packet(1, 13))
            self.assertEqual(sock.recv(100), packet(2, 13))

            # Invalid wire versions must not receive a response.
            invalid = bytearray(packet(1, 14))
            invalid[4] = 99
            sock.send(invalid)
            with self.assertRaises(socket.timeout):
                sock.recv(100)

            # A second endpoint cannot send Ping before its own handshake.
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as other:
                other.settimeout(0.2)
                other.connect(("127.0.0.1", self.port))
                other.send(packet(1, 15))
                with self.assertRaises(socket.timeout):
                    other.recv(100)

            # Original endpoint's session still responds.
            sock.send(packet(1, 16))
            self.assertEqual(sock.recv(100), packet(2, 16))

if __name__ == "__main__":
    unittest.main()
