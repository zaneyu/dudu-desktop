; Dudu Desktop — private, current-user (per-user) installer.
;
; This package installs only for the current Windows user, under
; {localappdata}\Programs\DuduDesktop, with no UAC elevation. It never
; creates the current-user Startup shortcut owned by
; StartupRegistrationService (Dudu Desktop Companion.lnk); that shortcut is
; created exclusively by the app's own in-app preference, at first run and
; across every upgrade of this installer. Uninstall is the one exception
; (review I7): the shortcut is deleted there, because nothing else can --
; the app that owns it is being removed, and a leftover Startup shortcut
; pointing at a deleted executable fails on every sign-in forever.
;
; Built with Inno Setup 7.1.0: `pwsh scripts/publish-windows.ps1` publishes
; src/Dudu.App then compiles this script with ISCC.exe.

[Setup]
#ifndef AppVersion
#define AppVersion "1.0.0"
#endif
AppId={{FC8FF35F-5A84-41EA-9E84-23A8EF06F1F6}
AppName=Dudu Desktop
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\DuduDesktop
DefaultGroupName=Dudu Desktop
PrivilegesRequired=lowest
; Brief said SetupArchitecture=x64 (not a real directive); controller ruling: ArchitecturesAllowed/ArchitecturesInstallIn64BitMode.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=DuduDesktop-{#AppVersion}-win-x64-private
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Dudu.App.exe
CloseApplications=yes
RestartApplications=no
SetupIconFile=assets\dudu.ico
WizardSmallImageFile=assets\wizard-small.bmp

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; The complete self-contained publish output (artifacts/publish/win-x64),
; produced by scripts/publish-windows.ps1 immediately before ISCC.exe runs.
; The wildcards below are deliberate: an exact per-file list of a ~530-file
; self-contained WinUI tree would break on every Windows App SDK/.NET servicing
; bump. Instead publish-windows.ps1 refuses to invoke ISCC unless every file in
; the publish tree matches its $PublishTreeAllowlist (Assert-PublishManifest)
; and the private pack holds only manifest.json + frames/**/*.png
; (Assert-PrivateReleaseAssetPack). Never compile this script by hand against
; an unvalidated publish directory.
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The private-dudu asset pack is deliberately NOT part of the csproj's
; Content Include (only Assets/Packs/fallback/** is), so dotnet publish
; never copies it. Copy it straight from source so the installed app ships
; both the fallback pack and the private pack.
Source: "..\src\Dudu.App\Assets\Packs\private-dudu\*"; DestDir: "{app}\Assets\Packs\private-dudu"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Current-user Start Menu shortcut. Deliberately placed directly under
; {userprograms} (not a DefaultGroupName subfolder) so its path matches
; tests/installer/installer-smoke.ps1, which checks for
; "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Dudu Desktop.lnk".
Name: "{code:GetStartMenuShortcutDir}\Dudu Desktop"; Filename: "{app}\Dudu.App.exe"
; Optional desktop shortcut, off by default.
Name: "{userdesktop}\Dudu Desktop"; Filename: "{app}\Dudu.App.exe"; Tasks: desktopicon

; No entry here ever references {userstartup}: the "launch at sign-in"
; shortcut (Dudu Desktop Companion.lnk) is owned entirely by
; StartupRegistrationService and the recipient's in-app preference. The
; installer must neither create nor delete it on install or upgrade — doing
; so here would fight the in-app toggle across upgrades. Uninstall is
; handled in [Code] (CurUninstallStepChanged), not here, because it must run
; whatever the user chose about keeping their data.

[Code]
const
  // The exact, and only, directory this installer is ever allowed to
  // delete user data from. Keep this in sync with AppPaths.ForCurrentUser()
  // in src/Dudu.App/Hosting/AppPaths.cs.
  DataRootFolderName = 'DuduDesktop';

var
  ShouldDeleteUserData: Boolean;

// ISCC requires a literal "\" in an [Icons] Name, so the override is a directory.
function GetStartMenuShortcutDir(Param: string): string;
begin
  Result := GetEnv('DUDU_START_MENU_SHORTCUT_DIR');
  if Result = '' then
    Result := ExpandConstant('{userprograms}');
end;

function GetDataRoot(): string;
begin
  // Installer smoke tests set the same override the app uses so all test data
  // stays in an isolated per-run directory. Normal installs keep the canonical
  // current-user location when the override is absent.
  Result := GetEnv('DUDU_DATA_ROOT');
  if Result = '' then
    Result := ExpandConstant('{localappdata}') + '\' + DataRootFolderName;
end;

function WantsDeleteUserDataParam(): Boolean;
begin
  // Both the silent and interactive uninstall paths go through this same
  // /DELETEUSERDATA=1 check before DeleteUserData() is ever called.
  Result := ExpandConstant('{param:DELETEUSERDATA|0}') = '1';
end;

// Deletes exactly {localappdata}\DuduDesktop, and only after validating
// that the resolved path genuinely ends in \DuduDesktop. This is the one
// safety check standing between a future edit and deleting an unrelated
// directory — never weaken or remove it. Inno Setup's CloseApplications
// contract closes files belonging to this installation before post-uninstall
// cleanup. Do not use image-wide process termination here: another Dudu
// process is out of scope.
procedure DeleteUserData();
var
  DataRoot, RequiredSuffix: string;
begin
  DataRoot := GetDataRoot();
  RequiredSuffix := '\' + DataRootFolderName;

  if (Length(DataRoot) < Length(RequiredSuffix)) or
     (CompareText(
        Copy(DataRoot, Length(DataRoot) - Length(RequiredSuffix) + 1, Length(RequiredSuffix)),
        RequiredSuffix) <> 0) then
    Exit;

  if DirExists(DataRoot) then
    DelTree(DataRoot, True, True, True);
end;

// Decides, once, whether this uninstall will delete user data:
//   1. /DELETEUSERDATA=1 on the command line always wins (silent or not).
//   2. A silent uninstall with no such parameter keeps data (same default
//      as the interactive checkbox below).
//   3. An interactive uninstall shows one page with a single checkbox,
//      "Keep my notes and settings", checked by default; unchecking it and
//      continuing is the only other way to delete data.
// Both of the two real outcomes funnel into the same DeleteUserData().
function InitializeUninstall(): Boolean;
var
  Form: TSetupForm;
  CheckBox: TNewCheckBox;
  UninstallButton, CancelButton: TNewButton;
begin
  Result := True;
  ShouldDeleteUserData := False;

  if WantsDeleteUserDataParam() then
  begin
    ShouldDeleteUserData := True;
    Exit;
  end;

  if UninstallSilent then
  begin
    ShouldDeleteUserData := False;
    Exit;
  end;

  // Inno Setup 7 signature: (ClientWidth, ClientHeight, KeepSizeX, KeepSizeY).
  // KeepSizeY = True: nothing on this form can grow vertically.
  Form := CreateCustomForm(ScaleX(380), ScaleY(150), False, True);
  try
    Form.Caption := 'Uninstall Dudu Desktop';
    Form.Position := poScreenCenter;
    Form.BorderStyle := bsDialog;

    CheckBox := TNewCheckBox.Create(Form);
    CheckBox.Parent := Form;
    CheckBox.Left := ScaleX(16);
    CheckBox.Top := ScaleY(16);
    CheckBox.Width := Form.ClientWidth - ScaleX(32);
    CheckBox.Height := ScaleY(34);
    CheckBox.Caption := 'Keep my notes and settings';
    CheckBox.Checked := True;

    UninstallButton := TNewButton.Create(Form);
    UninstallButton.Parent := Form;
    UninstallButton.Caption := 'Uninstall';
    UninstallButton.Width := ScaleX(85);
    UninstallButton.Height := ScaleY(23);
    UninstallButton.Left := Form.ClientWidth - ScaleX(182);
    UninstallButton.Top := Form.ClientHeight - ScaleY(36);
    UninstallButton.ModalResult := mrOk;
    UninstallButton.Default := True;
    Form.ActiveControl := UninstallButton;

    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.Caption := 'Cancel';
    CancelButton.Width := ScaleX(85);
    CancelButton.Height := ScaleY(23);
    CancelButton.Left := Form.ClientWidth - ScaleX(89);
    CancelButton.Top := Form.ClientHeight - ScaleY(36);
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;

    if Form.ShowModal() = mrOk then
      ShouldDeleteUserData := not CheckBox.Checked
    else
      Result := False;
  finally
    Form.Free;
  end;
end;

// Deletes the "launch at sign-in" shortcut StartupRegistrationService owns.
// Unconditional on the keep-my-data choice (review I7): the shortcut is not
// user data, it is a pointer to the executable being removed, and leaving it
// behind means a failed launch on every subsequent sign-in. Exactly this one
// path under {userstartup} is ever touched, and only if it exists. Installer
// smoke supplies a test-only path so it never removes an operator shortcut.
function GetStartupShortcutPath(): string;
begin
  Result := GetEnv('DUDU_STARTUP_SHORTCUT_PATH');
  if Result = '' then
    Result := ExpandConstant('{userstartup}\Dudu Desktop Companion.lnk');
end;

procedure DeleteStartupShortcut();
var
  ShortcutPath: string;
begin
  ShortcutPath := GetStartupShortcutPath();
  if FileExists(ShortcutPath) then
    DeleteFile(ShortcutPath);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DeleteStartupShortcut();
    if ShouldDeleteUserData then
      DeleteUserData();
  end;
end;
