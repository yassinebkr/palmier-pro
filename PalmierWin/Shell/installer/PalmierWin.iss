; PalmierWin installer - Inno Setup 6.
; Compiled by the release workflow:
;   ISCC /DAppVersion=1.2.3 PalmierWin.iss
; Input is the staged portable folder at ..\publish (PalmierShell.exe,
; PalmierCoreHost.dll, ffmpeg and Swift runtime DLLs).

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{B7E2A4F1-3C5D-4E6A-9F0B-1D2C3A4B5C6D}
AppName=PalmierWin
AppVersion={#AppVersion}
AppVerName=PalmierWin {#AppVersion}
AppPublisher=Yassine Bkr
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Yassine Bkr
VersionInfoProductName=PalmierWin
VersionInfoDescription=PalmierWin Setup
DefaultDirName={localappdata}\Programs\PalmierWin
DefaultGroupName=PalmierWin
; Per-user install: no UAC prompt for testers.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=PalmierWin-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The shell holds this mutex for its lifetime; setup can then offer to close
; a running instance instead of failing on locked files.
AppMutex=PalmierWinShell
CloseApplications=yes
UninstallDisplayName=PalmierWin

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Always created - the option is shown, checked and locked on the options page.
Name: "{autodesktop}\PalmierWin"; Filename: "{app}\PalmierShell.exe"; WorkingDir: "{app}"
Name: "{group}\PalmierWin"; Filename: "{app}\PalmierShell.exe"

[Run]
Filename: "{app}\PalmierShell.exe"; Description: "Launch PalmierWin"; Flags: nowait postinstall skipifsilent

[Code]
var
  OptionsPage: TWizardPage;
  VulkanOk: Boolean;

function VulkanLoaderPresent: Boolean;
begin
  { The Vulkan loader lives in System32 once any vendor driver is installed. }
  Result := FileExists(ExpandConstant('{sys}\vulkan-1.dll'));
end;

function InitializeSetup: Boolean;
begin
  VulkanOk := VulkanLoaderPresent;
  Result := True;
  if not VulkanOk then
    Result := MsgBox(
      'No Vulkan runtime was found on this PC (vulkan-1.dll is missing). '
      + 'PalmierWin needs a Vulkan-capable GPU and driver for preview, playback and export.'
      + #13#10#13#10
      + 'Install anyway?', mbCriticalError, MB_YESNO) = IDYES;
end;

procedure InitializeWizard;
var
  RequirementsText: TNewStaticText;
  VulkanStatus: TNewStaticText;
  DesktopCheck: TNewCheckBox;
  LogsCheck: TNewCheckBox;
  LogsNote: TNewStaticText;
begin
  OptionsPage := CreateCustomPage(wpWelcome, 'Requirements and options',
    'What PalmierWin needs, and what setup always does.');

  RequirementsText := TNewStaticText.Create(OptionsPage);
  RequirementsText.Parent := OptionsPage.Surface;
  RequirementsText.Left := 0;
  RequirementsText.Top := 0;
  RequirementsText.Width := OptionsPage.SurfaceWidth;
  RequirementsText.AutoSize := False;
  RequirementsText.WordWrap := True;
  RequirementsText.Caption :=
    'PalmierWin needs a Vulkan-capable GPU and driver. Without one the editor ' +
    'opens, but preview, playback and export stay unavailable.';
  RequirementsText.Height := 32;

  VulkanStatus := TNewStaticText.Create(OptionsPage);
  VulkanStatus.Parent := OptionsPage.Surface;
  VulkanStatus.Left := 0;
  VulkanStatus.Top := RequirementsText.Top + RequirementsText.Height + 4;
  VulkanStatus.Width := OptionsPage.SurfaceWidth;
  if VulkanOk then
    VulkanStatus.Caption := 'Vulkan runtime: found.'
  else
    VulkanStatus.Caption := 'Vulkan runtime: NOT found - install your GPU driver before running PalmierWin.';

  DesktopCheck := TNewCheckBox.Create(OptionsPage);
  DesktopCheck.Parent := OptionsPage.Surface;
  DesktopCheck.Left := 0;
  DesktopCheck.Top := VulkanStatus.Top + 28;
  DesktopCheck.Width := OptionsPage.SurfaceWidth;
  DesktopCheck.Caption := 'Create a desktop shortcut';
  DesktopCheck.Checked := True;
  DesktopCheck.Enabled := False;

  LogsCheck := TNewCheckBox.Create(OptionsPage);
  LogsCheck.Parent := OptionsPage.Surface;
  LogsCheck.Left := 0;
  LogsCheck.Top := DesktopCheck.Top + 24;
  LogsCheck.Width := OptionsPage.SurfaceWidth;
  LogsCheck.Caption := 'Enable diagnostic logs';
  LogsCheck.Checked := True;
  LogsCheck.Enabled := False;

  LogsNote := TNewStaticText.Create(OptionsPage);
  LogsNote.Parent := OptionsPage.Surface;
  LogsNote.Left := 18;
  LogsNote.Top := LogsCheck.Top + 20;
  LogsNote.Width := OptionsPage.SurfaceWidth - 18;
  LogsNote.AutoSize := False;
  LogsNote.WordWrap := True;
  LogsNote.Height := 32;
  LogsNote.Caption :=
    'Always on for test builds. Session logs are written to ' +
    '%APPDATA%\PalmierPro\logs - include them with any bug report.';
end;
