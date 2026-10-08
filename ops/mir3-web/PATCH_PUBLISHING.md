# Mir3 Zircon launcher patch publishing (HTTPS)

This document describes the production workflow that distributes game-client
patches to the launcher over HTTPS at `https://example.invalid/patch/`.
No credentials are stored in source control; the only secret involved is the
operator's SSH key, which lives on the operator's workstation only.

## Architecture

```
DESKTOP-I6LAPQ0 (Windows, operator)                LXC (mir3-web host)
+-----------------------------+                    +-----------------------------+
| PatchManager (WinForms)     |  1. build manifest | /opt/mir3-web/patch/        |
|  -> Launcher.Core manifest  |     + .gz payloads |   releases/<timestamp>/     |
|  -> .\Publish\ (staging)    |                    |   (PList.Bin, *.gz,         |
+-----------------------------+                    |    SHA256SUMS)              |
            | 2. ops/mir3-web/publish-patch.ps1    |        ^                    |
            |    (scp + ssh, operator's key)       |        | atomic switch     |
            v                                    |   current -> releases/...   |
                                                   |        |                    |
                                                   |  Mir3.Web static files      |
                                                   |  at /patch/PList.Bin        |
                                                   +-----------------------------+
```

1. **PatchManager** compares the clean client directory against the manifest
   it last staged in its `PublishDirectory`, gzip-compresses changed files into
   flat `*.gz` payloads (`Data\System.db` becomes `Data-System.db.gz`), and
   writes `PList.Bin` last via an atomic replace. The binary manifest layout
   is unchanged from the original Zircon pair: `string fileName, int64
   compressedLength, int32 checkSumLength, checkSum bytes` (UTF-8,
   length-prefixed). `Launcher.Core` is the single implementation shared by
   Launcher and PatchManager, so the two can never drift apart.
2. **`ops/mir3-web/publish-patch.ps1`** uploads the staged directory to
   `/opt/mir3-web/patch/releases/<ReleaseName>` on the LXC, verifies SHA256
   checksums on the server, then atomically repoints the `current` symlink
   (`ln -sfn` + `mv -Tf`), and finally checks the public HTTPS manifest URL.
3. **Mir3.Web** serves `PhysicalFileProvider` static content from
   `/opt/mir3-web/patch/current` at the `/patch` request path. The directory
   is created by `ops/mir3-web/install.sh`; the service account only needs
   read access and never writes there.

## One-time server setup (LXC)

```bash
# From the repo on any admin shell:
sudo MIR3_WEB_PATCH_ROOT=/opt/mir3-web/patch ops/mir3-web/install.sh
```

The patch root and its `releases/` directory are owned `root:root` mode
`0755`. The SSH account used for publishing therefore needs root (or sudo)
on the LXC — grant it out of band, e.g. an SSH key in
`/root/.ssh/authorized_keys` or a passwordless-sudo deploy user. Do not put
any of that into this repository.

The web service reads through the `current` symlink. The route is enabled at
service startup only if the directory exists, so **after the very first patch
publication run `sudo systemctl restart mir3-web`**; later publications need
no restart because the symlink swap is picked up per request.

## Publishing a patch (DESKTOP-I6LAPQ0)

1. Sync the repo and build the tooling (from a Developer PowerShell):
   ```powershell
   cd C:\...\zircon-mir3-portal
   dotnet build "Zircon Server.sln" -c Release
   dotnet test Launcher.Core.Tests\Launcher.Core.Tests.csproj -c Release
   ```
   The WinForms projects (Launcher, PatchManager) require the Windows
   Desktop SDK and DevExpress 25.2.6; they build only on Windows.
2. Prepare the clean client folder (exact copy of the client the launcher
   maintains) and run **PatchManager**:
   - *Clean Client*: path to the clean client directory.
   - *Publish Directory*: a local staging folder, e.g. `C:\Mir3Patch\Publish`.
   - Press **Upload Patch**. PatchManager compresses changed files and writes
     `PList.Bin` into the publish directory. Nothing leaves the machine.
3. Dry-run the publisher, then publish for real:
   ```powershell
   # Dry run: validates inputs and prints the release summary, touches nothing.
   pwsh ops\mir3-web\publish-patch.ps1 `
       -PatchDirectory C:\Mir3Patch\Publish `
       -SshTarget root@<LXC-IP> -DryRun

   # Publish: scp upload, remote sha256sum -c, atomic symlink switch,
   # public HTTPS verification of /patch/PList.Bin.
   pwsh ops\mir3-web\publish-patch.ps1 `
       -PatchDirectory C:\Mir3Patch\Publish `
       -SshTarget root@<LXC-IP>
   ```
   Optional overrides: `-ReleaseName <name>` (default UTC-ish timestamp,
   must match `^[A-Za-z0-9][A-Za-z0-9._-]*$`), `-RemoteRoot`,
   `-PublicBaseUrl`, `-SkipPublicVerify`. The script refuses to overwrite an
   existing release directory.
4. Verify from any machine:
   ```powershell
   curl.exe -fsS "https://example.invalid/patch/PList.Bin" -o PList.Bin
   # Optionally diff the manifest against the one PatchManager produced.
   ```

## Rebuilding the launcher / PatchManager Windows executables

No EXE artifacts are committed to this repository. To produce them on
DESKTOP-I6LAPQ0 (needs .NET 10 SDK + Windows Desktop SDK + DevExpress
25.2.6 licensed feed):

```powershell
dotnet publish Launcher\Launcher.csproj -c Release -r win-x64 --self-contained
dotnet publish PatchManager\PatchManager.csproj -c Release -r win-x64 --self-contained
# Outputs (before copying them anywhere, record checksums):
Get-FileHash Launcher\bin\Release\win-x64\publish\Launcher.exe -Algorithm SHA256
Get-FileHash PatchManager\bin\Release\win-x64\publish\PatchManager.exe -Algorithm SHA256
```

Distribute the launcher by placing `Launcher.exe` (and its DevExpress
assemblies) into the clean client so existing clients receive it through the
normal patch flow; the launcher self-updates when its own entry changes.

## Safety properties enforced by code/tests

- `PatchOrigin` accepts only absolute HTTPS URLs without embedded
  credentials, query, or fragment (`Launcher.Core.Tests/PatchOriginTests`).
- `PatchPaths` rejects rooted paths, drive letters, `..`, duplicate
  separators, trailing separators, wildcards, and control characters, and
  `ResolveClientPath` guarantees the target stays inside the client root
  (`PatchPathsTests`).
- `PatchManifest` caps entries (200k) and check-sum length (1 KiB), rejects
  truncation, and keeps the original binary wire format
  (`PatchManifestTests`).
- The launcher validates the live manifest the same way before touching the
  file system, writes `Version.bin` via a temp file + atomic replace, and
  persists zeroed check sums for failed downloads so the next run
  re-downloads only those files.
- `publish-patch.ps1` rejects reparse points in the patch path, refuses
  unexpected files, verifies remote checksums before the atomic symlink
  switch, and never mutates the operator's local patch directory. Ordering
  guarantees are pinned by `tests/ops/test-publish-patch.py`.
- `Mir3.Web` options validation rejects relative or traversal patch
  directories (`Mir3.Web.Tests/PatchStaticFilesTests`).
