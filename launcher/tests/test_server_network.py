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
