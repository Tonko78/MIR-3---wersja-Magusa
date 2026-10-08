# Launcher installer and manual downloads

The public portal uses these fixed routes:

| Route | Content | Source in `/opt/mir3-web/downloads` |
| --- | --- | --- |
| `/download` | Downloads page | Razor Page |
| `/download/launcher` | Small unsigned per-user Windows installer | `Mir3-Zircon-Launcher-Setup.exe` |
| `/download/client` | Complete ZIP without launcher | `Mir3-Zircon-Client-2026-09-22.zip` |
| `/download/client-7z` | Previous 7z archive (preserved) | `Mir3-Zircon-Client-2026-09-22.7z` |

The launcher fetches game payloads only from the public HTTPS `/patch/` repository. Do not put FTP, passwords, or other credentials into the installer or archives. The ZIP runs directly after extraction and has no automatic repair. SHA-256 hashes and exact byte counts are shown on the Downloads page when each verified regular file is available. HTTP ranges allow resuming large downloads.

## Windows installer

Build with Inno Setup 6 on an operator-controlled Windows workstation from the manifest-matching published `Launcher.exe` and `Patcher.exe`:

```powershell
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' "/DSourceDir=C:\path\to\verified-launcher-files" 'Mir3-Launcher.iss'
```

Check the actual installation path of `ISCC.exe` on the workstation rather than assuming the example path. The installer requests no elevation and defaults to `%LOCALAPPDATA%\Programs\Mir3 Zircon`. It creates a Start-menu shortcut, offers a desktop shortcut, and offers to launch the patching launcher on the completion screen. An unsigned installer can trigger a SmartScreen warning; signing would require an independently obtained code-signing certificate. Never advise disabling protection.

Re-running setup preserves game assets and player settings. Uninstall removes the installed launcher, patcher, and shortcuts; it deliberately leaves downloaded game files and `Zircon.ini` / `Data\Users.db`. After uninstall, a user who also wants to remove the game data can delete `%LOCALAPPDATA%\Programs\Mir3 Zircon` manually after backing up their settings. Do not remove that directory in the uninstaller.

## Client packaging safety

`package-client.ps1` requires an explicit `-SourcePath` (alias `-Source`) when packaging; `-ValidateOnly` does not require a source. Set `-ServerHost` to the operator's game host; the safe default is `127.0.0.1`, port `7000`. The same host is used to write and validate `Zircon.ini`, including extracted archives. Packaging does not execute the client unless `-VerifyLaunch` is supplied. The legacy `-SkipLaunch` switch still overrides launch verification. Use only reviewed local assets; `-TestMode` sanitizes a copy without archiving or launching it.

Deployment URLs in these templates use `example.invalid`. Replace them with your own configuration before deployment; supply `-PublicBaseUrl` to `publish-patch.ps1` for your public patch endpoint.

## ZIP creation and publication

Run `build-client-zip.py --client <reviewed-client-tree> --output <unique-hidden-stage-in-downloads>`. It rejects symlinks, unsafe Windows names, collisions, missing required game files and preexisting output. It excludes launcher binaries, debug symbols and stale metadata, then creates `SHA256SUMS.txt` from the included files. On the production LXC, first check free space; the source extracted client is large and must not be deleted while generating the ZIP. Independently verify ZIP CRC, all entries and source hashes, UTF-16LE `Zircon.ini`, no launcher binaries, and the final file SHA-256. Set stage ownership/read access for `mir3-web` and rename on the same filesystem to the configured published filename; never stream a partial stage. Preserve the original 7z and active patch release.

## Deployment and rollback

Keep artifacts in `/opt/mir3-web/downloads` separately from the portal release. Build and test the portal, then use the existing `ops/mir3-web/install.sh` process to stage and atomically activate the new release. Verify `/healthz`, homepage, Downloads page, three binary routes (including ranges and full SHA-256), patch manifest, registration, and both `mir3-web` and `zircon` services. If the new portal fails, use `ops/mir3-web/rollback.sh` to restore only the prior web release. Do not alter Nginx Proxy Manager, game services, mail, or MirDB. Retain both new artifacts for diagnosis even on rollback.
