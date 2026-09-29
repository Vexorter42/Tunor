; Tunor installer — clean distribution (no personal keys, no personal rules)
;
; Build it after packaging/stage.py has run:   ISCC packaging/tunor.iss
; Every path is relative to this file, so it works from any checkout.
;
; Renamed from Nyx in 1.10.0. AppId stays as it was: it is what makes an existing
; install upgrade in place instead of appearing twice in "Programs and Features".
#define MyAppName "Tunor"
#define MyAppVersion "1.10.2"
#define MyAppPublisher "Tunor"
#define MyAppExeName "Tunor.exe"
#define FormerExeName "Nyx.exe"
#define StageDir SourcePath + "..\build-out\dist"
#define OutputDirectory SourcePath + "..\build-out\out"
#define IconFile SourcePath + "..\ui\Assets\app.ico"

[Setup]
; Same AppId and install dir as before, so existing installs upgrade in place
; and keep their warp.conf / geo.conf / rules.json / settings.json.
AppId={{6F2A1C9E-4B7D-4E2A-9C1F-A1B2C3D4E5F6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Only new installs land here; an upgrade keeps the folder it already uses.
DefaultDirName={autopf}\Tunor
DisableProgramGroupPage=yes
DisableDirPage=no
OutputDir={#OutputDirectory}
OutputBaseFilename=Tunor-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\ui\{#MyAppExeName}
; OTA self-update: running processes are force-closed in [Code], the app is
; relaunched in [Run]; Restart Manager is not used (conflicts with tray apps).
CloseApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; The previous name's files and shortcuts. Without this the old Nyx.exe stays behind,
; still starts, and its Start-menu entry points at a program that is no longer updated.
Type: files; Name: "{app}\ui\{#FormerExeName}"
Type: files; Name: "{app}\ui\Nyx.dll"
Type: files; Name: "{app}\ui\Nyx.deps.json"
Type: files; Name: "{app}\ui\Nyx.runtimeconfig.json"
Type: files; Name: "{autoprograms}\Nyx.lnk"
Type: files; Name: "{autodesktop}\Nyx.lnk"
; Two helper scripts inherited from the SSnet build, removed in 1.10.1: they created a
; scheduled task under a name from two renames ago, pointing at a file that has not
; shipped for a long time — running one produced a broken autostart entry.
Type: files; Name: "{app}\build\install.bat"
Type: files; Name: "{app}\build\delete.bat"
; Artefacts of the older SSnet build (renamed app + dropped CLI)
Type: files; Name: "{app}\ui\SSnetUI.exe"
Type: files; Name: "{app}\ui\SSnetUI.dll"
Type: files; Name: "{app}\ui\SSnetUI.deps.json"
Type: files; Name: "{app}\ui\SSnetUI.runtimeconfig.json"
Type: files; Name: "{app}\SSnetCli.exe"
Type: files; Name: "{autoprograms}\SSnet.lnk"
Type: files; Name: "{autodesktop}\SSnet.lnk"

[Files]
; --- Program files: always updated ---
Source: "{#StageDir}\ui\*"; DestDir: "{app}\ui"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\build\*.bat"; DestDir: "{app}\build"; Flags: ignoreversion
Source: "{#StageDir}\build\restart-headless.vbs"; DestDir: "{app}\build"; Flags: ignoreversion
; sing-box: keep the installed engine if present. It is usually running (locked),
; and skipping it means an update never drops the tunnel. Bumping the engine is a
; deliberate action — switch both lines below to `ignoreversion` and add a sing-box
; taskkill in [Code] when a new engine build has to ship.
Source: "{#StageDir}\build\sing-box.exe"; DestDir: "{app}\build"; Flags: onlyifdoesntexist
; Says which engine build that file is. Same flag as the engine itself, so the two can
; never disagree; the app also compares the hash written here with the file it has.
Source: "{#StageDir}\build\sing-box.version"; DestDir: "{app}\build"; Flags: onlyifdoesntexist

; --- GPL compliance: licence texts for the bundled sing-box engine ---
Source: "{#StageDir}\licenses\*"; DestDir: "{app}\licenses"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#StageDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

; --- User data / preserved on upgrade ---
; Rule-set .srs files are NOT shipped: Tunor downloads them from itdoginfo/allow-domains
; on first run, so they are never redistributed by this installer.
Source: "{#StageDir}\settings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#StageDir}\update.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#StageDir}\build\config.json"; DestDir: "{app}\build"; Flags: onlyifdoesntexist
Source: "{#StageDir}\data\rules.json"; DestDir: "{app}\data"; Flags: onlyifdoesntexist
Source: "{#StageDir}\data\warp.conf"; DestDir: "{app}\data"; Flags: onlyifdoesntexist
Source: "{#StageDir}\data\geo.conf"; DestDir: "{app}\data"; Flags: onlyifdoesntexist

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\ui\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\ui\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Interactive install: offer a "launch" checkbox on the finished page
Filename: "{app}\ui\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
  Flags: nowait postinstall skipifsilent
; Silent install (OTA): relaunch automatically (postinstall entries don't run when silent)
Filename: "{app}\ui\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent

[Dirs]
Name: "{app}\data\rulesets"

[UninstallDelete]
; Everything the app writes after installation — the installer itself never ships these.
Type: filesandordirs; Name: "{app}\build\config.json.bak"
Type: files; Name: "{app}\build\config.json.version"
Type: filesandordirs; Name: "{app}\data\warp.conf.bak"
Type: filesandordirs; Name: "{app}\data\geo.conf.bak"
Type: filesandordirs; Name: "{app}\data\profiles"
Type: filesandordirs; Name: "{app}\data\rulesets"
Type: filesandordirs; Name: "{app}\licenses"
Type: files; Name: "{app}\crash.log"
Type: files; Name: "{app}\THIRD-PARTY-NOTICES.md"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var rc: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Close the app under every name it has had, so its files can be replaced.
    // sing-box is intentionally left running — the update keeps the tunnel up.
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Tunor.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Nyx.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im SSnetUI.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  end;
end;
