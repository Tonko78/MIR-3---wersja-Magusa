import hashlib
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

SCRIPT = Path(__file__).resolve().parents[2] / "ops" / "mir3-web" / "build-client-zip.py"

REQUIRED = ["Zircon.exe", "Zircon.dll", "Data/System.db", "Zircon.ini"]


def make_valid_client(client: Path):
    (client / "Data").mkdir(parents=True, exist_ok=True)
    (client / "Zircon.exe").write_bytes(b"game exe")
    (client / "Zircon.dll").write_bytes(b"game dll")
    (client / "Data" / "System.db").write_bytes(b"game data")
    (client / "Zircon.ini").write_bytes("﻿[Zircon]\r\n".encode("utf-16-le"))
    # Files that must be excluded from the published ZIP:
    (client / "Launcher.exe").write_bytes(b"launcher")
    (client / "Patcher.exe").write_bytes(b"patcher")
    (client / "Mir3.pdb").write_bytes(b"debug symbols")
    (client / "SHA256SUMS.txt").write_bytes(b"stale checksum list")
    (client / "HERMES-BUILD-2026-09-01").write_text("stale build metadata")


def parse_sums(data: bytes):
    entries = {}
    for line in data.decode("utf-8").splitlines():
        digest, _, name = line.partition("  ")
        entries[name] = digest
    return entries


class BuildClientZipTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.client = self.root / "client"
        self.client.mkdir()
        self.out = self.root / "Mir3-Zircon-Client.zip"

    def build(self):
        return subprocess.run([sys.executable, str(SCRIPT), "--client", str(self.client),
                               "--output", str(self.out)], capture_output=True, text=True)

    def test_windows_name_validation_without_filesystem_constraints(self):
        spec = importlib.util.spec_from_file_location("build_client_zip", SCRIPT)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        for name in ("bad|name.exe", "bad\\name.exe", "aux.txt"):
            with self.subTest(name=name):
                self.assertIsNotNone(module.check_windows_name(name))
        self.assertIsNone(module.check_windows_name("Zircon.exe"))

    def test_builds_zip_without_launcher_binaries_or_stale_metadata(self):
        make_valid_client(self.client)
        result = self.build()
        self.assertEqual(result.returncode, 0, result.stderr)
        with zipfile.ZipFile(self.out) as archive:
            self.assertIsNone(archive.testzip())
            names = set(archive.namelist())
            for required in REQUIRED:
                self.assertIn(required, names)
            self.assertNotIn("Launcher.exe", names)
            self.assertNotIn("Patcher.exe", names)
            self.assertNotIn("Mir3.pdb", names)
            self.assertNotIn("SHA256SUMS.txt.tmp", names)
            # The source's stale metadata is gone; only the regenerated list remains.
            sums_name = "SHA256SUMS.txt"
            self.assertIn(sums_name, names)
            stale = [n for n in names if n.startswith("HERMES-BUILD")]
            self.assertEqual(stale, [])
            sums = parse_sums(archive.read(sums_name))
            expected = {
                "Zircon.exe": hashlib.sha256(b"game exe").hexdigest(),
                "Zircon.dll": hashlib.sha256(b"game dll").hexdigest(),
                "Data/System.db": hashlib.sha256(b"game data").hexdigest(),
                "Zircon.ini": hashlib.sha256("﻿[Zircon]\r\n".encode("utf-16-le")).hexdigest(),
            }
            self.assertEqual(sums, expected)
            # Every listed checksum regenerates from the matching ZIP entry.
            for name, digest in sums.items():
                self.assertIn(name, names)
                self.assertEqual(hashlib.sha256(archive.read(name)).hexdigest(), digest)

    def test_rejects_symlink_without_publishing_zip(self):
        make_valid_client(self.client)
        try:
            os.symlink(self.client / "Zircon.exe", self.client / "Redirect.exe")
        except OSError as error:
            if getattr(error, "winerror", None) == 1314:
                self.skipTest("Windows symlink privilege unavailable")
            raise
        result = self.build()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.out.exists())

    def test_rejects_symlinked_directory(self):
        make_valid_client(self.client)
        link = self.client / "Linked"
        try:
            os.symlink(self.client / "Data", link, target_is_directory=True)
        except OSError as error:
            if getattr(error, "winerror", None) == 1314:
                self.skipTest("Windows symlink privilege unavailable")
            raise
        result = self.build()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.out.exists())

    def test_rejects_preexisting_output_without_touching_it(self):
        make_valid_client(self.client)
        self.out.write_bytes(b"caller-owned content")
        result = self.build()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.out.read_bytes(), b"caller-owned content")

    def test_rejects_output_inside_client(self):
        make_valid_client(self.client)
        nested = self.client / "out.zip"
        result = subprocess.run([sys.executable, str(SCRIPT), "--client", str(self.client),
                                 "--output", str(nested)], capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(nested.exists())

    def test_missing_required_file_fails(self):
        make_valid_client(self.client)
        (self.client / "Data" / "System.db").unlink()
        result = self.build()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.out.exists())

    def test_casefold_collision_fails(self):
        make_valid_client(self.client)
        (self.client / "Data" / "Extra.db").write_bytes(b"one")
        dup = self.client / "DATA"
        dup.mkdir(exist_ok=True)
        if (dup / "EXTRA.DB").exists():
            self.skipTest("fixture requires a case-sensitive filesystem")
        (dup / "EXTRA.DB").write_bytes(b"two")
        result = self.build()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.out.exists())

    @unittest.skipIf(os.name == "nt", "Windows cannot represent this POSIX filename fixture")
    def test_unsafe_windows_name_fails(self):
        make_valid_client(self.client)
        (self.client / "bad|name.exe").write_bytes(b"nope")
        result = self.build()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.out.exists())

    @unittest.skipIf(os.name == "nt", "Windows cannot represent this POSIX filename fixture")
    def test_windows_path_separator_in_filename_fails(self):
        make_valid_client(self.client)
        (self.client / "bad\\name.exe").write_bytes(b"nope")
        result = self.build()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.out.exists())

    @unittest.skipIf(os.name == "nt", "Windows cannot represent this POSIX filename fixture")
    def test_reserved_windows_device_name_fails(self):
        make_valid_client(self.client)
        (self.client / "aux.txt").write_bytes(b"nope")
        result = self.build()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.out.exists())


if __name__ == "__main__":
    unittest.main()
