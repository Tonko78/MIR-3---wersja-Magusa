; Per-user bootstrap for the public HTTPS Mir3 patch repository.
; Build: see PUBLICATION-INPUTS.md; SourceDir and PatchOrigin are required.
; SourceDir must contain only the manifest-matching Launcher.exe and Patcher.exe.
#ifndef SourceDir
  #error Pass /DSourceDir with the reviewed launcher binaries.
#endif

; No network access: validate the operator-supplied origin during compilation.
#ifndef PatchOrigin
  #error Pass /DPatchOrigin=https://your-reviewed-patch-host (no trailing slash).
#endif
#ifndef PythonExe
  #define PythonExe "python.exe"
#endif
; Prevent Windows argument quoting ambiguity before invoking the validator.
#if Pos('"', PatchOrigin) || Pos('\', PatchOrigin)
  #error Invalid PatchOrigin characters.
#endif
#if Exec(PythonExe, '"' + AddBackslash(SourcePath) + 'validate-patch-origin.py" "' + PatchOrigin + '"', SourcePath, 1) != 0
  #error PatchOrigin validation failed; supply an explicit HTTPS origin and a working PythonExe.
#endif

[Setup]
AppId=Mir3-Zircon-Launcher
AppName=Mir3 Zircon
AppVersion=1.0.0
AppPublisher=Mir3 Zircon
AppPublisherURL=https://example.invalid
DefaultDirName={localappdata}\Programs\Mir3 Zircon
DefaultGroupName=Mir3 Zircon
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\Launcher.exe
OutputBaseFilename=Mir3-Zircon-Launcher-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\Launcher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Patcher.exe"; DestDir: "{app}"; Flags: ignoreversion

[INI]
Filename: "{app}\Launcher.ini"; Section: "Patcher"; Key: "Host"; String: "{#PatchOrigin}/"; Flags: createkeyifdoesntexist; Check: not FileExists(ExpandConstant('{app}\Launcher.ini'))

[Icons]
Name: "{group}\Mir3 Zircon"; Filename: "{app}\Launcher.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Mir3 Zircon"; Filename: "{app}\Launcher.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Launcher.exe"; Description: "Start Mir3 Zircon and download the game"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
