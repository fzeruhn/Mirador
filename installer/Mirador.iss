; Mirador's installer (Inno Setup 6). Built by tools\build-installer.ps1 from artifacts\bundle (tools\build-bundle.ps1):
;   ISCC /DAppVersion=<version> installer\Mirador.iss
;
; {app} is Program Files\Mirador, admin-only: the helper service runs from it as LocalSystem and makes its Monado the
; OpenXR runtime of every app (Mirador.md section 7.4), so there is no folder choice. If one ever comes back, {app} needs
; a protected DACL (Administrators and SYSTEM full control, Users read and execute).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define Bundle "..\artifacts\bundle"
#define ServiceName "MiradorHelper"

[Setup]
AppId={{CE00D9D4-6F37-49A0-B17D-16BAA7531E77}
AppName=Mirador
AppVersion={#AppVersion}
AppVerName=Mirador {#AppVersion}
AppPublisher=Finn Zeruhn
AppPublisherURL=https://github.com/fzeruhn/Mirador
DefaultDirName={autopf}\Mirador
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
OutputDir=..\artifacts
OutputBaseFilename=Mirador-{#AppVersion}-setup
SetupIconFile=..\src\Mirador.App\Assets\Mirador.ico
UninstallDisplayIcon={app}\Mirador.exe
UninstallDisplayName=Mirador
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
MinVersion=10.0.19041

[InstallDelete]
; What an older version (or the dev install's helper.new) left there goes, so no stale file is loaded.
Type: filesandordirs; Name: "{app}\helper"
Type: filesandordirs; Name: "{app}\helper.new"
Type: filesandordirs; Name: "{app}\monado"
Type: filesandordirs; Name: "{app}\layer"
Type: filesandordirs; Name: "{app}\standin"
Type: filesandordirs; Name: "{app}\tools"

[Files]
Source: "{#Bundle}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Mirador"; Filename: "{app}\Mirador.exe"

[Registry]
; The bundled layer, registered as an implicit layer; it acts only when DREAMAIR_VK_LAYER=1 (in Monado's process).
Root: HKLM64; Subkey: "SOFTWARE\Khronos\Vulkan\ImplicitLayers"; ValueType: dword; ValueName: "{app}\layer\dreamair_vk_layer.json"; ValueData: "0"; Flags: uninsdeletevalue
; The bundled Monado, listed as an available OpenXR runtime.
Root: HKLM64; Subkey: "SOFTWARE\Khronos\OpenXR\1\AvailableRuntimes"; ValueType: dword; ValueName: "{app}\monado\runtime\openxr_monado.json"; ValueData: "0"; Flags: uninsdeletevalue
; Where the stand-in finds Mirador when no user has run it yet.
Root: HKLM64; Subkey: "SOFTWARE\Mirador"; ValueType: string; ValueName: "AppPath"; ValueData: "{app}\Mirador.exe"; Flags: uninsdeletevalue uninsdeletekeyifempty

[Run]
Filename: "{app}\Mirador.exe"; Description: "Start Mirador"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
const
  ImplicitLayers = 'SOFTWARE\Khronos\Vulkan\ImplicitLayers';
  OpenXrKey = 'SOFTWARE\Khronos\OpenXR\1';
  MiradorKey = 'SOFTWARE\Mirador';
  QuitMirador = 'Mirador is running. Quit it from its tray icon (Quit), then press Retry.';

{ Mirador holds its own files and, while Monado runs, Pimax's software is stopped: it must be quit, not pulled away. }
function MiradorQuit: Boolean;
begin
  while CheckForMutexes('Local\Mirador') do
    if SuppressibleMsgBox(QuitMirador, mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  Result := True;
end;

function InitializeSetup: Boolean;
begin
  Result := MiradorQuit;
end;

function InitializeUninstall: Boolean;
begin
  Result := MiradorQuit;
end;

function Sc(const Args: String): Integer;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

function ServiceExists: Boolean;
begin
  Result := Sc('query {#ServiceName}') = 0;
end;

{ Stops the helper and waits (up to 30 s) until it has; a service that isn't there is fine. }
procedure StopHelper;
var
  I, Code: Integer;
begin
  if not ServiceExists then
    Exit;
  Sc('stop {#ServiceName}');
  for I := 1 to 60 do
  begin
    { sc query's exit code doesn't say the state: ask PowerShell for it, quietly. }
    if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -Command "if ((Get-Service {#ServiceName}).Status -eq ''Stopped'') { exit 0 } else { exit 1 }"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
      Exit;
    Sleep(500);
  end;
end;

// Before copying: only Program Files\Mirador, which only administrators can change (a /DIR= elsewhere is refused:
// the helper runs from that folder as SYSTEM); then the helper stops, since an update or the dev install
// (tools\dev-install-helper.ps1) may run it from there.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{autopf}\Mirador')) <> 0 then
  begin
    Result := 'Mirador installs only to ' + ExpandConstant('{autopf}\Mirador') + ': its helper service runs from there as SYSTEM, so only administrators may change that folder.';
    Exit;
  end;

  StopHelper;
  Result := '';
end;

{ The standalone layer's registration (its own install, Mirador.md section 4.1 item 1) goes: Mirador's replaces it. }
function HasSuffix(const Text, Suffix: String): Boolean;
begin
  Result := (Length(Text) >= Length(Suffix))
    and (Copy(Text, Length(Text) - Length(Suffix) + 1, Length(Suffix)) = Suffix);
end;

procedure RemoveOtherLayers;
var
  Names: TArrayOfString;
  I: Integer;
  Ours, Name: String;
begin
  Ours := Lowercase(ExpandConstant('{app}\layer\dreamair_vk_layer.json'));
  if RegGetValueNames(HKLM64, ImplicitLayers, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      Name := Lowercase(Names[I]);
      if (Name <> Ours) and HasSuffix(Name, '\dreamair_vk_layer.json') then
        RegDeleteValue(HKLM64, ImplicitLayers, Names[I]);
    end;
end;

{ A step of the helper's setup that failed is said, with what to do: Mirador can't stop Pimax's software without it. }
procedure HelperStep(const What: String; Code: Integer);
begin
  if Code <> 0 then
    SuppressibleMsgBox('Mirador''s helper service couldn''t be ' + What + ' (sc.exe error ' + IntToStr(Code) + '). ' +
      'Restart Windows and run this setup again; until then Mirador can''t stop Pimax''s software for Monado.',
      mbError, MB_OK, IDOK);
end;

procedure InstallHelper;
var
  BinPath: String;
begin
  BinPath := '"\"' + ExpandConstant('{app}\helper\Mirador.Helper.exe') + '\""';
  if ServiceExists then
    HelperStep('set up', Sc('config {#ServiceName} binPath= ' + BinPath + ' start= auto DisplayName= "Mirador helper"'))
  else
    HelperStep('installed', Sc('create {#ServiceName} binPath= ' + BinPath + ' start= auto DisplayName= "Mirador helper"'));
  Sc('description {#ServiceName} "Does the steps Mirador needs an administrator for: Pimax''s service and the OpenXR runtime."');
  HelperStep('started', Sc('start {#ServiceName}'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RemoveOtherLayers;
    InstallHelper;
  end;
end;

{ Uninstall: put the OpenXR runtime back if Mirador's is active, then take the helper out. Profiles, logs and
  calibration backups in %LOCALAPPDATA%\Mirador stay. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Active, Ours, Recorded: String;
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  Ours := ExpandConstant('{app}\monado\runtime\openxr_monado.json');
  if RegQueryStringValue(HKLM64, OpenXrKey, 'ActiveRuntime', Active) and (CompareText(Active, Ours) = 0) then
  begin
    if SuppressibleMsgBox('Monado is the active OpenXR runtime. Switch back to the runtime that was active before?',
      mbConfirmation, MB_YESNO, IDYES) = IDYES then
    begin
      { First the helper goes quiet, so no request races the restore. }
      StopHelper;
      if not RegQueryStringValue(HKLM64, MiradorKey, 'PreviousActiveRuntime', Recorded) then
        Recorded := '';
      if not Exec(ExpandConstant('{app}\helper\Mirador.Helper.exe'), '--restore-runtime', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        Code := -1;
      { However that went: put back what was recorded, if Mirador's runtime is still the active one. }
      if (Recorded <> '') and RegQueryStringValue(HKLM64, OpenXrKey, 'ActiveRuntime', Active) and (CompareText(Active, Ours) = 0) then
        RegWriteStringValue(HKLM64, OpenXrKey, 'ActiveRuntime', Recorded);
    end;

    { Never left pointing at a file this uninstall removes: no runtime rather than a broken one. }
    if RegQueryStringValue(HKLM64, OpenXrKey, 'ActiveRuntime', Active) and (CompareText(Active, Ours) = 0) then
      RegDeleteValue(HKLM64, OpenXrKey, 'ActiveRuntime');
  end;

  StopHelper;
  Sc('delete {#ServiceName}');
  RegDeleteValue(HKLM64, MiradorKey, 'PreviousActiveRuntime');
end;
