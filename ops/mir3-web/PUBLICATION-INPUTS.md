# Publication inputs and launcher bootstrap

Both `build-client-zip.py` and `create-patch.py` require a reviewed sanitized
client staging tree, never a working player/server tree. They share
`publication_input.py`: private/backup filenames are rejected before any file
content is opened (including Users.db, .env variants, private keys and backups).
Zircon.ini is inspected as UTF-8 or BOM-marked UTF-16. The exact remembered
fields cleared by package-client.ps1 must be empty (`RememberedEMail`,
`RememberedPassword`) and `RememberDetails` must be False if present.
Validation never edits the input. Run package-client.ps1's sanitization on a
separate staging copy first; do not point publication builders at C:/BBB.

## Installer build

Install Python 3 and Inno Setup on the build workstation. Provide the actual
reviewed HTTPS patch origin explicitly (there is no operational default):

```powershell
ISCC.exe /DSourceDir="C:\reviewed\bootstrap" /DPythonExe="C:\Python314\python.exe" /DPatchOrigin="https://patch.example.invalid" ops\mir3-web\Mir3-Launcher.iss
```

The reserved example hostname is a placeholder: replace it with your approved
origin. SourceDir contains the reviewed Launcher.exe and Patcher.exe only.
PatchOrigin accepts `https://hostname[:port]`, without trailing slash, path,
userinfo, query or fragment. At compile time Inno runs the local
validate-patch-origin.py and rejects nonzero exit status. This performs no DNS
lookup, network request, download, or payload execution.

For a new installation the installer creates Launcher.ini with `[Patcher]`
`Host=<validated origin>/`. Any existing Launcher.ini is left untouched,
including blank or custom Host settings; an operator must review and repair
such existing configuration manually. The INI is not deleted on uninstall.

Validation can also be run independently:

```powershell
python ops\mir3-web\validate-patch-origin.py https://patch.example.invalid
```
