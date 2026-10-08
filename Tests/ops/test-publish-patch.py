#!/usr/bin/env python3
"""Focused tests for the Mir3 HTTPS patch repository publisher."""
from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPOSITORY_ROOT / "ops" / "mir3-web" / "publish-patch.ps1"


def powershell() -> str | None:
    return shutil.which("pwsh") or shutil.which("powershell")


def run_script(*arguments: str) -> subprocess.CompletedProcess[str]:
    executable = powershell()
    if executable is None:
        raise unittest.SkipTest("PowerShell is not installed; static checks still run")
    return subprocess.run(
        [executable, "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT), "-SshCommand", "__TEST_REMOTE_FORBIDDEN__", "-ScpCommand", "__TEST_REMOTE_FORBIDDEN__", "-CurlCommand", "__TEST_REMOTE_FORBIDDEN__", *arguments],
        cwd=REPOSITORY_ROOT,
        text=True,
        errors="replace",
        capture_output=True,
        check=False,
    )


def make_patch_fixture(root: Path) -> None:
    (root / "PList.Bin").write_bytes(b"manifest-bytes")
    (root / "Zircon.exe.gz").write_bytes(b"payload-one")
    (root / "Data-System.db.gz").write_bytes(b"payload-two")


class PublishPatchTests(unittest.TestCase):
    def test_static_contract_is_present(self) -> None:
        self.assertTrue(SCRIPT.is_file(), "publish-patch.ps1 must exist")
        source = SCRIPT.read_text(encoding="utf-8")
        for required in (
            "PatchDirectory",
            "SshTarget",
            "ReleaseName",
            "RemoteRoot",
            "PList.Bin",
            "SHA256SUMS",
            "Get-FileHash",
            "Assert-NoReparsePointsInPath",
            "Assert-NoReparsePointsBelow",
            "DryRun",
            "SkipPublicVerify",
            "sha256sum -c SHA256SUMS",
            "mv -Tf",
            "current",
            "releases",
            "publicBaseUrl",
            "https://example.invalid/patch/",
        ):
            self.assertIn(required, source, f"missing publish contract: {required}")

    def test_checksum_verification_happens_before_symlink_switch(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertLess(
            source.index("sha256sum -c SHA256SUMS"),
            source.index("mv -Tf"),
            "remote checksum verification must run before the live symlink switch",
        )

    def test_public_verification_is_last(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertLess(
            source.index("mv -Tf"),
            source.index("$CurlCommand -fsSI"),
            "the public HTTPS check must run after the symlink switch",
        )

    def test_dry_run_exits_before_any_remote_command(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        dry_run = source.index("if ($DryRun)")
        self.assertLess(dry_run, source.index("& $ScpCommand"))
        self.assertLess(dry_run, source.index("& $SshCommand $SshTarget"))

    def test_dry_run_reports_patch_summary(self) -> None:
        if powershell() is None:
            self.skipTest("PowerShell is not installed")
        with tempfile.TemporaryDirectory(prefix="mir3-publish-dryrun-") as temporary:
            patch = Path(temporary) / "patch"
            patch.mkdir()
            make_patch_fixture(patch)
            result = run_script(
                "-PatchDirectory", str(patch),
                "-SshTarget", "unused@example.invalid",
                "-ReleaseName", "20260101000000",
                "-DryRun",
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            report = json.loads(result.stdout)
            self.assertEqual(report["mode"], "DryRun")
            self.assertEqual(report["release"], "20260101000000")
            self.assertEqual(report["payloadCount"], 2)
            self.assertFalse(report["published"])

    def test_missing_manifest_is_rejected(self) -> None:
        if powershell() is None:
            self.skipTest("PowerShell is not installed")
        with tempfile.TemporaryDirectory(prefix="mir3-publish-red-") as temporary:
            patch = Path(temporary) / "patch"
            patch.mkdir()
            (patch / "Zircon.exe.gz").write_bytes(b"payload")
            result = run_script(
                "-PatchDirectory", str(patch),
                "-SshTarget", "unused@example.invalid",
            )
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertRegex(result.stdout + result.stderr, r"(?i)PList\.Bin")

    def test_unexpected_files_are_rejected(self) -> None:
        if powershell() is None:
            self.skipTest("PowerShell is not installed")
        with tempfile.TemporaryDirectory(prefix="mir3-publish-stale-") as temporary:
            patch = Path(temporary) / "patch"
            patch.mkdir()
            make_patch_fixture(patch)
            (patch / "notes.txt").write_text("stale", encoding="utf-8")
            result = run_script(
                "-PatchDirectory", str(patch),
                "-SshTarget", "unused@example.invalid",
            )
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertRegex(result.stdout + result.stderr, r"(?i)unexpected files")

    def test_release_name_must_be_safe(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("[ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]", source)

    def test_local_patch_directory_is_never_mutated(self) -> None:
        if powershell() is None:
            self.skipTest("PowerShell is not installed")
        with tempfile.TemporaryDirectory(prefix="mir3-publish-immutable-") as temporary:
            patch = Path(temporary) / "patch"
            patch.mkdir()
            make_patch_fixture(patch)
            before = {p.name: p.read_bytes() for p in patch.iterdir()}
            result = run_script(
                "-PatchDirectory", str(patch),
                "-SshTarget", "unused@example.invalid",
                "-DryRun",
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            after = {p.name: p.read_bytes() for p in patch.iterdir()}
            self.assertEqual(before, after)


if __name__ == "__main__":
    unittest.main(verbosity=2)
