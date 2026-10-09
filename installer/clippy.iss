; Clippy installer.
;
; Built by the release workflow, which passes the tag in:
;   ISCC.exe /DPublishVersion=1.3.5 installer\clippy.iss
; A human running it by hand gets 1.3.5, which is fine for a smoke test.
;
; Every path is relative to this file, so the script does not care where the repository sits.

#ifndef PublishVersion
  ; Only used when ISCC is run by hand. The release workflow always passes the tag as
  ; /DPublishVersion=, so the two can never disagree about what release this is.
  #define PublishVersion "1.3.5"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define AppName "Clippy"
#define AppExe "Clippy.exe"

[Setup]
AppId={{7C4B1D2E-9F3A-4A5B-8C6D-1E2F3A4B5C6D}
AppName={#AppName}
AppVersion={#PublishVersion}
AppPublisher=Clippy Authors
;   Icon for the installer itself and for the shortcuts it creates. Every path in this script is
;   relative to it, so the same relative walk reaches into src\assets for the application icon.
SetupIconFile=..\src\assets\app.ico
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
OutputBaseFilename=Clippy-Setup
OutputDir=..\artifacts
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Per-user install into LocalAppData. An instant replay recorder has no business asking for admin,
; and a UAC prompt is the fastest way to make a user distrust an unsigned installer.
PrivilegesRequired=lowest

; The whole point of the silent-update path: a running Clippy.exe is locked, so the installer has to
; be able to ask Windows to close it. Restart Manager does this for us.
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=no

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; A per-user install unregisters itself under the user, not under HKLM.
Uninstallable=yes

[Languages]
; The installer's own strings come from the Russian message file; the task and file descriptions in
; this script are already written in Russian to match.
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "autostart"; Description: "Запускать {#AppName} при старте Windows"; GroupDescription: "Дополнительно:"
Name: "desktopicon"; Description: "Создать значок на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
; One entry with a wildcard, not a hand-kept list: the publish folder is the definition of what ships,
; and a list would silently fall behind the moment a file is added. setup.exe is skipped so a nested
; installer inside the archive can never install itself.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "setup.exe,mp4-selftest-*,Tests\*,Tests,*.pdb"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon
; Created only when the user ticked the task, and removed only when they untick it, so uninstalling
; never leaves a shortcut pointing at an application that is gone.
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: autostart

[Run]
; postinstall is what puts the "Run now" checkbox on the Finish page. `nowait` because Clippy goes on
; to live in the tray; the installer must not sit there waiting for a tray application to exit.
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppName} сейчас"; Flags: nowait postinstall skipifsilent

[Code]
// Comments here use // and not { }, because Inno's preprocessor treats a brace as the start of a
// constant reference -- a braced comment inside a [Code] section is a compile error, not a comment.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupLink: String;
begin
  // Removed on uninstall only when it is actually there: the autostart task may never have been
  // ticked, and deleting a file that does not exist is noise at best.
  if CurUninstallStep = usPostUninstall then
  begin
    StartupLink := ExpandConstant('{userstartup}\{#AppName}.lnk');
    if FileExists(StartupLink) then
      DeleteFile(StartupLink);
  end;
end;
