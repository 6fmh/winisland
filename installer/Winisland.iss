#ifndef PublishDirectory
  #error PublishDirectory must point to a self-contained publish.
#endif
#ifndef InstallerOutputDirectory
  #define InstallerOutputDirectory "..\dist\installer"
#endif
#ifndef AppVersion
  #define AppVersion "0.6.0"
#endif
#ifndef AppRuntime
  #define AppRuntime "win-x64"
#endif

[Setup]
AppId={{4966D65C-F470-49A9-B206-6D4A9C710BDA}
AppName=Winisland
AppVersion={#AppVersion}
AppPublisher=6fmh
AppPublisherURL=https://github.com/6fmh/winisland
AppSupportURL=https://github.com/6fmh/winisland/issues
AppUpdatesURL=https://github.com/6fmh/winisland/releases/latest
LicenseFile=..\LICENSE
AppComments=A quiet little space at the top of your Windows desktop.
DefaultDirName={localappdata}\Programs\Winisland
DefaultGroupName=Winisland
PrivilegesRequired=lowest
MinVersion=10.0.19041
#if AppRuntime == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir={#InstallerOutputDirectory}
OutputBaseFilename=Winisland-Setup-{#AppVersion}-{#AppRuntime}
SetupIconFile=..\src\Winisland\Assets\Winisland.ico
UninstallDisplayIcon={app}\Winisland.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=yes
CloseApplications=yes
CloseApplicationsFilter=Winisland.exe
RestartApplications=no
Uninstallable=yes
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "swedish"; MessagesFile: "compiler:Languages\Swedish.isl"

[Tasks]
Name: "startwithwindows"; Description: "Start Winisland immediately when I sign in"; Flags: checkedonce
Name: "startmenu"; Description: "Create a Start menu shortcut"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Winisland"; Filename: "{app}\Winisland.exe"; Tasks: startmenu
Name: "{userdesktop}\Winisland"; Filename: "{app}\Winisland.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\CDP"; ValueType: dword; ValueName: "DragTrayEnabled"; ValueData: "0"; Check: IsWindows11

[Run]
Filename: "{app}\Winisland.exe"; Description: "Launch Winisland"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Winisland.exe"; Parameters: "--configure-startup remove-owned"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveSigninTask"
Filename: "{app}\Winisland.exe"; Parameters: "--unregister-browser-host"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "UnregisterBrowserHost"

[Code]
const
  DropTrayKey = 'Software\Microsoft\Windows\CurrentVersion\CDP';
  BackupKey = 'Software\Winisland\Installer';
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  StartupConfigurationFailed: Boolean;

function SendMessageTimeout(Wnd: HWND; Msg, WParam: LongWord;
  LParam: String; Flags, Timeout: LongWord; var MessageResult: LongWord): LongInt;
  external 'SendMessageTimeoutW@user32.dll stdcall';

function IsWindows11: Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := Version.Build >= 22000;
end;

procedure NotifyDropTrayChanged;
var
  MessageResult: LongWord;
begin
  { Notify the shell without closing Explorer or interrupting file operations. }
  SendMessageTimeout($FFFF, $001A, 0, DropTrayKey, $0002, 1000, MessageResult);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  OriginalValue, OriginalPresent: Cardinal;
begin
  Result := '';
  if not IsWindows11 then Exit;
  { Keep the first install's backup across upgrades and reinstalls. }
  if RegQueryDWordValue(HKCU, BackupKey, 'DropTrayOriginalPresent', OriginalPresent) then begin
    Log('Preserving an existing Drop tray backup.');
    Exit;
  end;

  OriginalPresent := 0;
  if RegValueExists(HKCU, DropTrayKey, 'DragTrayEnabled') then begin
    if not RegQueryDWordValue(HKCU, DropTrayKey, 'DragTrayEnabled', OriginalValue) then begin
      Result := 'Windows Drop tray has an unexpected setting value. Setup could not save it.';
      Exit;
    end;
    OriginalPresent := 1;
    if not RegWriteDWordValue(HKCU, BackupKey, 'DropTrayOriginalValue', OriginalValue) then begin
      Result := 'Setup could not save the previous Windows Drop tray setting.';
      Exit;
    end;
  end;
  if not RegWriteDWordValue(HKCU, BackupKey, 'DropTrayOriginalPresent', OriginalPresent) then
    Result := 'Setup could not save the previous Windows Drop tray setting.';
  Log(Format('Saved Drop tray original presence: %d', [OriginalPresent]));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  StartupChoice: String;
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then begin
    if IsWindows11 then NotifyDropTrayChanged;
    { In-app updates preserve the saved preference; an interactive install
      applies the choice from its Tasks page. The app owns the logon task and
      atomically saves the setting without starting any windows. }
    if ExpandConstant('{param:UPDATE|0}') = '1' then StartupChoice := 'preserve'
    else if WizardIsTaskSelected('startwithwindows') then StartupChoice := 'true'
    else StartupChoice := 'false';
    if not Exec(ExpandConstant('{app}\Winisland.exe'), '--configure-startup ' + StartupChoice,
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then begin
      StartupConfigurationFailed := True;
      RaiseException('Setup could not configure sign-in startup.');
    end;
    if ResultCode <> 0 then begin
      StartupConfigurationFailed := True;
      RaiseException('Setup could not save the sign-in preference. Please try again.');
    end;
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  if StartupConfigurationFailed then Result := 1 else Result := 0;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  CurrentValue, OriginalValue, OriginalPresent: Cardinal;
  StartupCommand, InstalledStartupCommand: String;
  Restored: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Restored := True;
  Log('Restoring the saved Drop tray state after uninstall.');
  if RegQueryDWordValue(HKCU, BackupKey, 'DropTrayOriginalPresent', OriginalPresent) then begin
    Log(Format('Saved Drop tray original presence: %d', [OriginalPresent]));
    { Respect a subsequent change the user made in Windows Settings. }
    if RegQueryDWordValue(HKCU, DropTrayKey, 'DragTrayEnabled', CurrentValue) and (CurrentValue = 0) then begin
      if OriginalPresent = 0 then
        Restored := RegDeleteValue(HKCU, DropTrayKey, 'DragTrayEnabled')
      else if RegQueryDWordValue(HKCU, BackupKey, 'DropTrayOriginalValue', OriginalValue) then
        Restored := RegWriteDWordValue(HKCU, DropTrayKey, 'DragTrayEnabled', OriginalValue)
      else
        Restored := False;
      NotifyDropTrayChanged;
    end;
    if Restored then begin
      Log(Format('Cleared Drop tray original value: %d', [Ord(RegDeleteValue(HKCU, BackupKey, 'DropTrayOriginalValue'))]));
      Log(Format('Cleared Drop tray original presence: %d', [Ord(RegDeleteValue(HKCU, BackupKey, 'DropTrayOriginalPresent'))]));
      Log(Format('Removed empty Drop tray backup key: %d', [Ord(RegDeleteKeyIfEmpty(HKCU, BackupKey))]));
    end else
      Log('Could not restore the Windows Drop tray setting; its backup has been kept.');
  end;
  InstalledStartupCommand := '"' + ExpandConstant('{app}\Winisland.exe') + '" --background';
  if RegQueryStringValue(HKCU, StartupKey, 'Winisland', StartupCommand) and
     (CompareText(StartupCommand, InstalledStartupCommand) = 0) then
    RegDeleteValue(HKCU, StartupKey, 'Winisland');
end;

procedure InitializeWizard;
begin
  if IsWindows11 then
    WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption + #13#10#13#10 +
      'Setup turns off Windows Drop tray so it does not cover the notch when you drag files. ' +
      'Uninstalling restores the previous setting unless you have changed it yourself.';
end;
