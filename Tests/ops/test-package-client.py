#!/usr/bin/env python3
"""Focused tests for the sanitized Mir3 Windows client packager."""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SCRIPT = REPOSITORY_ROOT / "ops" / "mir3-web" / "package-client.ps1"


def powershell() -> str | None:
    return shutil.which("pwsh") or shutil.which("powershell")


def run_script(*arguments: str) -> subprocess.CompletedProcess[str]:
    executable = powershell()
    if executable is None:
        raise unittest.SkipTest("PowerShell is not installed; static checks still run")
    return subprocess.run(
        [executable, "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT), *arguments],
        cwd=REPOSITORY_ROOT,
        text=True,
        errors="replace",  # Windows PowerShell diagnostics use the local code page.
        capture_output=True,
        check=False,
    )


def make_fixture(root: Path) -> None:
    (root / "Zircon.ini").write_text(
        "[Network]\n"
        "UseNetworkConfig=False\n"
        "IPAddress=192.0.2.44\n"
        "Port=1234\n\n"
        "[Login]\n"
        "RememberedEMail=player@example.test\n"
        "RememberedPassword=remembered-secret\n"
        "RememberDetails=True\n",
        encoding="utf-16",
    )
    (root / "client.before-20260925").write_text("old", encoding="utf-8")
    (root / "backup-ui-settings.json").write_text("old", encoding="utf-8")
    (root / "Errors").mkdir()
    (root / "Errors" / "error.log").write_text("error", encoding="utf-8")
    (root / "backup-private").mkdir()
    (root / "backup-private" / "private-token.txt").write_text(
        "sensitive backup content", encoding="utf-8"
    )
    for name in ("Logs.txt", "Chat Logs.txt", "screenshot.png", "crash.dmp"):
        (root / name).write_text("fixture", encoding="utf-8")
    (root / "readme.txt").write_text("safe client files", encoding="utf-8")


def make_fake_sevenzip(path: Path) -> None:
    path.write_text(
        "param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)\n"
        "$operation = $Arguments[0]\n"
        "if ($operation -eq 'a') { [IO.File]::WriteAllText($Arguments[3], 'new-archive'); exit 0 }\n"
        "if ($operation -eq 't') { exit 0 }\n"
        "if ($operation -eq 'x') {\n"
        "    $output = $Arguments | Where-Object { $_ -like '-o*' } | Select-Object -First 1\n"
        "    $root = $output.Substring(2)\n"
        "    New-Item -ItemType Directory -Path $root -Force | Out-Null\n"
        "    [IO.File]::WriteAllText((Join-Path $root 'Zircon.ini'), \"[Network]`nUseNetworkConfig=True`nIPAddress=127.0.0.1`nPort=7000`n\")\n"
        "    [IO.File]::WriteAllBytes((Join-Path $root 'Zircon.exe'), [byte[]](77,90))\n"
        "    exit 0\n"
        "}\n"
        "exit 1\n",
        encoding="utf-8",
    )


class PackageClientTests(unittest.TestCase):
    def test_operator_host_is_used_for_normalization_and_validation(self) -> None:
        with tempfile.TemporaryDirectory(prefix="mir3-operator-host-") as temporary:
            root = Path(temporary)
            source = root / "source"
            source.mkdir()
            make_fixture(source)
            staging = root / "staging"
            result = run_script("-TestMode", "-Source", str(source),
                                "-StagingPath", str(staging),
                                "-ServerHost", "example.invalid")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("IPAddress=example.invalid", (staging / "Zircon.ini").read_text(encoding="utf-16"))
            result = run_script("-ValidateOnly", str(staging), "-ServerHost", "example.invalid")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            result = run_script("-ValidateOnly", str(staging))
            self.assertNotEqual(result.returncode, 0)

    def test_static_contract_is_present(self) -> None:
        self.assertTrue(SCRIPT.is_file(), "package-client.ps1 must exist")
        source = SCRIPT.read_text(encoding="utf-8")
        for required in (
            "ValidateOnly",
            "Write-InspectedText",
            "DryRun",
            "RememberedEMail",
            "RememberedPassword",
            "RememberDetails",
            "Errors",
            "Logs.txt",
            "Chat Logs.txt",
            "*.png",
            "dmp|mdmp|hdmp|dump",
            "127.0.0.1",
            "7000",
            "7z",
            " t ",
            "-Arguments @('x'",
            ".sha256",
            ".json",
            "Get-FileHash",
            "MainWindowTitle",
            "SkipLaunch",
            "Stop-Process",
            "Remove-SafeGeneratedTree",
            "Remove-SafeGeneratedFile",
            "Assert-PathsSeparated",
        ):
            self.assertIn(required, source, f"missing packaging contract: {required}")

    def test_public_network_configuration_is_required(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("Test-CanonicalNetworkConfig", source)
        self.assertIn("Normalize-NetworkConfig", source)
        self.assertIn("Test-CanonicalNetworkConfig -Content $iniContent", source)
        self.assertIn("UseNetworkConfig=True", source)

    def test_network_validation_matches_case_sensitive_client_parser(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("$section.HeaderLine -cne '[Network]'", source)
        self.assertIn("$line -ceq 'UseNetworkConfig=True'", source)
        self.assertIn('$line -ceq "IPAddress=$ServerHost"', source)
        self.assertIn("$line -ceq 'Port=7000'", source)

    def test_validate_only_rejects_network_case_variants(self) -> None:
        with tempfile.TemporaryDirectory(prefix="mir3-package-network-case-") as temporary:
            staging = Path(temporary) / "staging"
            staging.mkdir()
            (staging / "Zircon.ini").write_text(
                "[network]\n"
                "usenetworkconfig=True\n"
                "ipaddress=127.0.0.1\n"
                "port=7000\n",
                encoding="utf-16",
            )
            result = run_script("-ValidateOnly", str(staging))
            if powershell() is None:
                self.skipTest("PowerShell is not installed")
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertRegex(result.stdout + result.stderr, r"(?i)network|canonical|validation")

    def test_archive_is_built_and_published_from_a_unique_temporary_path(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("$temporaryArchive", source)
        self.assertIn("Join-Path $archiveParent", source)
        self.assertIn("[System.IO.File]::Replace", source)
        self.assertIn("[System.IO.File]::Move", source)
        self.assertIn("Get-ArchiveMetadata -Archive $temporaryArchive -MetadataDirectory $releaseDirectory", source)
        self.assertIn("Publish-Release", source)
        self.assertIn("TestFailAfterArchivePublish", source)
        self.assertIn("rollback", source.lower())
        self.assertIn("-xr!$StagingMarkerName", source)
        self.assertNotIn("'-mx=9', $archiveFull", source)
        self.assertNotIn("Invoke-SevenZip -Executable $sevenZip -WorkingDirectory $stageFull `\n            -Arguments @('a', '-t7z', '-mx=9', $archiveFull", source)
        self.assertLess(source.index("-Operation 'test'"), source.index("Publish-Release -ReleaseDirectory"))

    def test_publication_happens_after_metadata_extraction_and_launch(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        publish = source.index("Publish-Release -ReleaseDirectory")
        self.assertLess(source.index("Get-ArchiveMetadata -Archive $temporaryArchive"), publish)
        self.assertLess(source.index("-Operation 'extract'"), publish)
        self.assertLess(source.index("Invoke-ClientValidation -Root $extractPath"), publish)
        self.assertLess(source.index("Invoke-CleanLaunch -ExtractedRoot $extractPath"), publish)

    def test_publication_failure_preserves_existing_release(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("TestFailAfterArchivePublish", source)
        self.assertIn("$publishedNames", source)
        self.assertIn("$movedExistingNames", source)
        self.assertIn("Restore-ReleaseBackup", source)

    def test_injected_publication_failure_restores_archive_and_sidecars(self) -> None:
        if powershell() is None:
            self.skipTest("PowerShell is not installed")
        with tempfile.TemporaryDirectory(prefix="mir3-package-rollback-") as temporary:
            root = Path(temporary)
            source = root / "source"
            source.mkdir()
            make_fixture(source)
            (source / "Zircon.exe").write_bytes(b"MZ")
            fake_sevenzip = root / "fake-sevenzip.ps1"
            make_fake_sevenzip(fake_sevenzip)
            archive = root / "Mir3-Zircon-Client-rollback.7z"
            archive.write_bytes(b"known-good-archive")
            (root / (archive.name + ".sha256")).write_text("known-good-sha", encoding="utf-8")
            (root / (archive.name + ".json")).write_text("known-good-json", encoding="utf-8")
            before = {
                path.name: path.read_bytes()
                for path in (archive, Path(str(archive) + ".sha256"), Path(str(archive) + ".json"))
            }
            result = run_script(
                "-Source", str(source),
                "-Destination", str(archive),
                "-SevenZipPath", str(fake_sevenzip),
                "-SkipLaunch",
                "-TestFailAfterArchivePublish",
                "-WorkRoot", str(root / "work"),
            )
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("test failure after archive publication", (result.stdout + result.stderr).lower())
            for path in (archive, Path(str(archive) + ".sha256"), Path(str(archive) + ".json")):
                self.assertEqual(path.read_bytes(), before[path.name], path.name)

    def test_path_and_cleanup_guards_cover_ancestors_and_owned_staging(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        for required in (
            "Assert-NoReparsePointsInPath",
            "Split-Path -LiteralPath $current",
            "sourceResolved",
            "stagingResolved",
            "ForceCleanup",
            ".mir3-client-staging-owner",
            ".mir3-client-workspace-owner",
            "existing staging path is not owned",
        ):
            self.assertIn(required, source, f"missing path-safety contract: {required}")

    def test_clean_launch_requires_live_client_window_title(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn(r"Mir3 \u2014 Wersja Magusa", source)
        self.assertIn("$process.HasExited", source)
        self.assertIn("$process.MainWindowHandle -ne [IntPtr]::Zero", source)
        self.assertIn("$process.MainWindowTitle", source)
        self.assertNotIn("-match '(?i)login|sign[ -]?in'", source)

    def test_clean_launch_uses_the_client_identity_from_target_form(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        target_form = (REPOSITORY_ROOT / "Client" / "TargetForm.cs").read_text(encoding="utf-8")
        self.assertIn('Text = "Mir3 — Wersja Magusa"', target_form)
        self.assertIn(r"^Mir3 \u2014 Wersja Magusa(?:\s+-\s+.*)?$", source)
        self.assertIn("$process.HasExited", source)
        self.assertIn("$process.MainWindowHandle -ne [IntPtr]::Zero", source)
        self.assertIn("$process.MainWindowTitle", source)
        self.assertNotIn("-match '(?i)login|sign[ -]?in'", source)

    def test_validate_only_rejects_unsanitized_fixture(self) -> None:
        with tempfile.TemporaryDirectory(prefix="mir3-package-red-") as temporary:
            staging = Path(temporary) / "staging"
            staging.mkdir()
            make_fixture(staging)
            result = run_script("-ValidateOnly", str(staging))
            combined = result.stdout + result.stderr
            self.assertNotEqual(result.returncode, 0, combined)
            self.assertRegex(combined, r"(?i)remembered|unsafe|validation")

    def test_directory_sanitization_checks_forbidden_names_before_continue(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        validation = source[
            source.index("function Invoke-ClientValidation"):source.index("function Remove-PrivateClientFiles")
        ]
        directory_branch = validation.index("if ($entry.PSIsContainer)")
        forbidden_check = validation.index(
            "Test-ForbiddenClientName -Name $entry.Name", directory_branch
        )
        directory_continue = validation.index("continue", directory_branch)
        self.assertLess(forbidden_check, directory_continue)
        removal = source[
            source.index("function Remove-PrivateClientFiles"):source.index(
                "function Clear-RememberedCredentials"
            )
        ]
        self.assertIn("Test-ForbiddenClientName -Name $entry.Name", removal)

    def test_validate_only_rejects_forbidden_backup_directory_with_sensitive_content(self) -> None:
        with tempfile.TemporaryDirectory(prefix="mir3-package-backup-directory-") as temporary:
            staging = Path(temporary) / "staging"
            staging.mkdir()
            (staging / "Zircon.ini").write_text(
                "[Network]\n"
                "UseNetworkConfig=True\n"
                "IPAddress=127.0.0.1\n"
                "Port=7000\n",
                encoding="utf-8",
            )
            backup = staging / "backup-private"
            backup.mkdir()
            (backup / "private-token.txt").write_text(
                "sensitive backup content", encoding="utf-8"
            )
            result = run_script("-ValidateOnly", str(staging))
            if powershell() is None:
                self.skipTest("PowerShell is not installed")
            combined = result.stdout + result.stderr
            self.assertNotEqual(result.returncode, 0, combined)
            self.assertRegex(combined, r"(?i)backup|forbidden|validation")

    def test_test_mode_sanitizes_and_validates_a_copy(self) -> None:
        with tempfile.TemporaryDirectory(prefix="mir3-package-green-") as temporary:
            root = Path(temporary)
            source = root / "source"
            staging = root / "staging"
            archive = root / "Mir3-Zircon-Client-test.7z"
            source.mkdir()
            make_fixture(source)
            result = run_script(
                "-TestMode",
                "-Source",
                str(source),
                "-Destination",
                str(archive),
                "-StagingPath",
                str(staging),
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertTrue(staging.is_dir())
            actual_ini = (staging / "Zircon.ini").read_text(encoding="utf-16").replace("\r\n", "\n")
            self.assertEqual(
                actual_ini,
                "[Network]\nUseNetworkConfig=True\nIPAddress=127.0.0.1\nPort=7000\n\n[Login]\nRememberedEMail=\nRememberedPassword=\nRememberDetails=False\n",
            )
            self.assertFalse((staging / "Errors").exists())
            self.assertFalse((staging / "backup-private").exists())
            for name in (
                "client.before-20260925",
                "backup-ui-settings.json",
                "Logs.txt",
                "Chat Logs.txt",
                "screenshot.png",
                "crash.dmp",
            ):
                self.assertFalse((staging / name).exists(), name)
            self.assertTrue((staging / "readme.txt").is_file())

            report = json.loads(result.stdout)
            self.assertEqual(report["mode"], "TestMode")
            self.assertTrue(report["validation"]["passed"])
            self.assertFalse(report["archive"]["created"])

    def test_test_mode_normalizes_network_case_and_spacing_variants(self) -> None:
        with tempfile.TemporaryDirectory(prefix="mir3-package-network-normalize-") as temporary:
            root = Path(temporary)
            source = root / "source"
            staging = root / "staging"
            source.mkdir()
            (source / "Zircon.ini").write_text(
                "[network]\n"
                " UseNetworkConfig = false\n"
                "ipaddress = 192.0.2.44\n"
                "Port = 1234\n\n"
                "[Login]\nRememberedEMail=\nRememberedPassword=\nRememberDetails=False\n",
                encoding="utf-16",
            )
            result = run_script(
                "-TestMode",
                "-Source", str(source),
                "-StagingPath", str(staging),
            )
            if powershell() is None:
                self.skipTest("PowerShell is not installed")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            actual_ini = (staging / "Zircon.ini").read_text(encoding="utf-16").replace("\r\n", "\n")
            self.assertEqual(
                actual_ini,
                "[Network]\nUseNetworkConfig=True\nIPAddress=127.0.0.1\nPort=7000\n\n"
                "[Login]\nRememberedEMail=\nRememberedPassword=\nRememberDetails=False\n",
            )

    def test_existing_unmarked_staging_is_refused(self) -> None:
        with tempfile.TemporaryDirectory(prefix="mir3-package-owned-") as temporary:
            root = Path(temporary)
            source = root / "source"
            staging = root / "caller-owned"
            source.mkdir()
            staging.mkdir()
            make_fixture(source)
            (staging / "do-not-delete.txt").write_text("caller data", encoding="utf-8")
            result = run_script(
                "-TestMode",
                "-Source",
                str(source),
                "-StagingPath",
                str(staging),
            )
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertTrue((staging / "do-not-delete.txt").exists())
            self.assertRegex(result.stdout + result.stderr, r"(?i)not owned|refusing")

    def test_reparse_source_root_and_ancestor_are_rejected(self) -> None:
        if not hasattr(os, "symlink"):
            self.skipTest("symlinks are unavailable")
        with tempfile.TemporaryDirectory(prefix="mir3-package-reparse-") as temporary:
            root = Path(temporary)
            real_parent = root / "real"
            source = real_parent / "source"
            real_parent.mkdir()
            source.mkdir()
            make_fixture(source)
            alias = root / "alias"
            try:
                alias.symlink_to(real_parent, target_is_directory=True)
            except (OSError, NotImplementedError) as error:
                self.skipTest(f"cannot create symlink: {error}")
            result = run_script(
                "-TestMode",
                "-Source",
                str(alias / "source"),
                "-StagingPath",
                str(root / "staging"),
            )
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertRegex(result.stdout + result.stderr, r"(?i)reparse|symlink|path")

    def test_reparse_staging_root_is_rejected(self) -> None:
        if not hasattr(os, "symlink"):
            self.skipTest("symlinks are unavailable")
        with tempfile.TemporaryDirectory(prefix="mir3-package-staging-reparse-") as temporary:
            root = Path(temporary)
            source = root / "source"
            target = root / "real-staging"
            source.mkdir()
            target.mkdir()
            make_fixture(source)
            alias = root / "staging-alias"
            try:
                alias.symlink_to(target, target_is_directory=True)
            except (OSError, NotImplementedError) as error:
                self.skipTest(f"cannot create symlink: {error}")
            result = run_script(
                "-TestMode",
                "-Source",
                str(source),
                "-StagingPath",
                str(alias),
            )
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertRegex(result.stdout + result.stderr, r"(?i)reparse|symlink|path")


if __name__ == "__main__":
    unittest.main(verbosity=2)
