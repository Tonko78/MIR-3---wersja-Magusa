import gzip
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "ops" / "mir3-web" / "create-patch.py"
VERIFY = Path(__file__).resolve().parents[2] / "ops" / "mir3-web" / "verify-patch.py"


def names_from_manifest(path: Path):
    names = []
    with path.open("rb") as stream:
        while (first := stream.read(1)):
            length, shift, byte = 0, 0, first[0]
            while True:
                length |= (byte & 127) << shift
                if byte < 128:
                    break
                byte = stream.read(1)[0]
                shift += 7
            name = stream.read(length).decode("utf-8")
            compressed, hash_len = struct.unpack("<qi", stream.read(12))
            checksum = stream.read(hash_len)
            assert len(checksum) == 16
            names.append((name, compressed))
    return names


class PatchCreationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.client = self.root / "client"
        self.client.mkdir()
        self.out = self.root / "output"

    def build(self):
        return subprocess.run([sys.executable, str(SCRIPT), "--client", str(self.client),
                               "--output", str(self.out)], capture_output=True, text=True)

    def test_real_file_round_trip_and_mutable_files_excluded(self):
        (self.client / "Data").mkdir()
        (self.client / "Data" / "System.db").write_bytes(b"game data")
        (self.client / "Zircon.ini").write_bytes(b"user settings")
        (self.client / "Launcher.exe").write_bytes(b"launcher")
        result = self.build()
        self.assertEqual(result.returncode, 0, result.stderr)
        entries = names_from_manifest(self.out / "PList.Bin")
        self.assertEqual([name for name, _ in entries], ["Data\\System.db", "Launcher.exe", "Zircon.ini"])
        self.assertEqual(gzip.decompress((self.out / "Data-System.db.gz").read_bytes()), b"game data")
        self.assertEqual(gzip.decompress((self.out / "Launcher.exe.gz").read_bytes()), b"launcher")
        self.assertEqual(gzip.decompress((self.out / "Zircon.ini.gz").read_bytes()), b"user settings")
        verify = subprocess.run([sys.executable, str(VERIFY), "--directory", str(self.out),
                                 "--client", str(self.client)], capture_output=True, text=True)
        self.assertEqual(verify.returncode, 0, verify.stderr)
        (self.out / "Launcher.exe.gz").write_bytes(b"corrupted")
        verify = subprocess.run([sys.executable, str(VERIFY), "--directory", str(self.out),
                                 "--client", str(self.client)], capture_output=True, text=True)
        self.assertNotEqual(verify.returncode, 0)

    def test_flat_name_collision_fails_without_manifest(self):
        (self.client / "Data").mkdir()
        (self.client / "Data" / "System.db").write_bytes(b"one")
        (self.client / "Data-System.db").write_bytes(b"two")
        result = self.build()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.out / "PList.Bin").exists())

    def test_rejects_symlink_and_existing_output(self):
        (self.client / "Launcher.exe").write_bytes(b"launcher")
        self.out.mkdir()
        self.assertNotEqual(self.build().returncode, 0)
        self.out.rmdir()
        try:
            os.symlink(self.client / "Launcher.exe", self.client / "Redirect.exe")
        except OSError as error:
            if getattr(error, "winerror", None) == 1314:
                self.skipTest("Windows symlink privilege unavailable")
            raise
        self.assertNotEqual(self.build().returncode, 0)
        self.assertFalse((self.out / "PList.Bin").exists())


if __name__ == "__main__":
    unittest.main()
