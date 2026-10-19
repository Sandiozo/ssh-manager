; SSH Manager installer (Inno Setup 6). Built by build.ps1, which first puts the app, the server and
; HiddenRun into ..\build\{app,server,tools}.
;  - Roles: the app (manage other PCs) and/or the server (let other PCs manage this one).
;  - Downloads and installs the .NET 8 Desktop Runtime and the WebView2 Runtime if the app needs them.
;  - The server role turns on Windows' OpenSSH Server and starts SSH Manager at logon (server-setup.ps1).
;  - Also used for updates: run silently (/VERYSILENT) it keeps the roles chosen before.

#ifndef Version
  #define Version "0.0.0"
#endif
#define BuildDir "..\build"

[Setup]
AppId={{8F3C2A51-6D4E-4B7A-9C1D-2E5F7A8B9C01}
AppName=SSH Manager
AppVersion={#Version}
AppVerName=SSH Manager {#Version}
AppPublisher=Sandiozo
AppPublisherURL=https://github.com/Sandiozo/ssh-manager
AppSupportURL=https://github.com/Sandiozo/ssh-manager/issues
AppUpdatesURL=https://github.com/Sandiozo/ssh-manager/releases
VersionInfoVersion={#Version}
DefaultDirName={autopf}\SSH Manager
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#BuildDir}
OutputBaseFilename=SSHManager-Setup
SetupIconFile=..\app\SSHManager\app.ico
UninstallDisplayIcon={app}\App\SSHManager.exe
UninstallDisplayName=SSH Manager
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=yes

[Types]
Name: "both"; Description: "Both: use SSH Manager here, and let other PCs manage this PC"
Name: "app"; Description: "App only: manage other PCs from this one"
Name: "server"; Description: "Managed only: let other PCs manage this PC"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "app"; Description: "SSH Manager app: see and manage your other PCs from this one"; Types: both app
Name: "server"; Description: "Let other PCs manage this PC: turns on Windows' OpenSSH Server and runs SSH Manager in the background"; Types: both server

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Components: app

[Files]
Source: "{#BuildDir}\app\*"; DestDir: "{app}\App"; Components: app; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#BuildDir}\server\*"; DestDir: "{app}\Server"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\ensure-running.ps1"; DestDir: "{app}\Server"; Components: server; Flags: ignoreversion
Source: "{#BuildDir}\tools\HiddenRun.exe"; DestDir: "{app}\Tools"; Components: server; Flags: ignoreversion
Source: "server-setup.ps1"; DestDir: "{app}\Tools"; Components: server; Flags: ignoreversion
Source: "server-stop.ps1"; DestDir: "{app}\Tools"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SSH Manager"; Filename: "{app}\App\SSHManager.exe"; Components: app
Name: "{autodesktop}\SSH Manager"; Filename: "{app}\App\SSHManager.exe"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Tools\server-setup.ps1"" -InstallDir ""{app}"" -User ""{code:InstallingUser}"""; \
  StatusMsg: "Setting up the SSH server. The first time, Windows downloads OpenSSH Server, which can take 5-15 minutes..."; Components: server; Flags: runhidden waituntilterminated
Filename: "{app}\App\SSHManager.exe"; Description: "Open SSH Manager"; Components: app; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Tools\server-stop.ps1"" -Remove"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveServer"

[UninstallDelete]
Type: files; Name: "{app}\Server\setup.log"

[Code]
const
  DotNetUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2Key = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

var
  DownloadPage: TDownloadWizardPage;

function InstallingUser(Param: String): String;
begin
  Result := GetEnv('USERDOMAIN') + '\' + GetUserNameString;
end;

{ .NET Desktop Runtime 8 or newer (x64) }
function HasDotNetDesktop: Boolean;
var
  Rec: TFindRec;
  Major: Integer;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\*'), Rec) then
  try
    repeat
      Major := StrToIntDef(Copy(Rec.Name, 1, Pos('.', Rec.Name) - 1), 0);
      if Major >= 8 then Result := True;
    until Result or not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

function HasWebView2: Boolean;
var
  V: String;
begin
  Result := (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\' + WebView2Key, 'pv', V) and (V <> '') and (V <> '0.0.0.0'))
    or (RegQueryStringValue(HKCU, 'Software\' + WebView2Key, 'pv', V) and (V <> '') and (V <> '0.0.0.0'));
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading what SSH Manager needs...', @OnDownloadProgress);
end;

function RunInstaller(const Name, Params, What: String): Boolean;
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing ' + What + '...';
  Result := Exec(ExpandConstant('{tmp}\') + Name, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) and ((Code = 0) or (Code = 3010) or (Code = 1638));
  if not Result then
    SuppressibleMsgBox('Installing ' + What + ' failed (code ' + IntToStr(Code) + '). SSH Manager may not start until it is installed.',
      mbError, MB_OK, IDOK);
end;

{ Download missing runtimes when the app is being installed. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  NeedDotNet, NeedWebView: Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or not WizardIsComponentSelected('app') then Exit;
  NeedDotNet := not HasDotNetDesktop;
  NeedWebView := not HasWebView2;
  if not (NeedDotNet or NeedWebView) then Exit;
  DownloadPage.Clear;
  if NeedDotNet then DownloadPage.Add(DotNetUrl, 'dotnet-desktop-runtime.exe', '');
  if NeedWebView then DownloadPage.Add(WebView2Url, 'webview2-setup.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      if NeedDotNet then RunInstaller('dotnet-desktop-runtime.exe', '/install /quiet /norestart', 'the .NET Desktop Runtime');
      if NeedWebView then RunInstaller('webview2-setup.exe', '/silent /install', 'the WebView2 Runtime');
    except
      if SuppressibleMsgBox('Downloading a required component failed:' + #13#10 + GetExceptionMessage + #13#10#13#10 +
        'Continue anyway? (SSH Manager may not start until it is installed.)', mbError, MB_YESNO, IDNO) = IDNO then
        Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

{ Stop a running server before its files are replaced (the app is closed by Restart Manager). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec('schtasks.exe', '/end /tn "SSH Manager"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec('taskkill.exe', '/f /im SSHManagerServer.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

function AskDisableSshd(const Why: String): String;
begin
  Result := '';
  if (not WizardSilent) and (MsgBox(Why + #13#10#13#10 +
      'Also turn off Windows'' OpenSSH Server on this PC? Choose No if you use SSH to reach this PC for anything else.',
      mbConfirmation, MB_YESNO) = IDYES) then
    Result := ' -DisableSshd';
end;

function RunServerStop(const Args: String): Boolean;
var
  Code: Integer;
begin
  Result := Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Tools\server-stop.ps1') + '"' + Args,
    '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ Changing roles: when "Let other PCs manage this PC" was installed before and is now unticked,
  remove it (the installer itself only adds files). }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardIsComponentSelected('server') and DirExists(ExpandConstant('{app}\Server')) then
  begin
    WizardForm.StatusLabel.Caption := 'Removing "Let other PCs manage this PC"...';
    RunServerStop(' -Remove' + AskDisableSshd('Other PCs can no longer manage this PC.'));
    DelTree(ExpandConstant('{app}\Server'), True, True, True);
    DeleteFile(ExpandConstant('{app}\Tools\HiddenRun.exe'));
    DeleteFile(ExpandConstant('{app}\Tools\server-setup.ps1'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if (CurUninstallStep = usUninstall) and DirExists(ExpandConstant('{app}\Server')) and not UninstallSilent and
     (MsgBox('Also turn off Windows'' OpenSSH Server on this PC? Choose No if you use SSH to reach this PC for anything else.',
       mbConfirmation, MB_YESNO) = IDYES) then
    Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Tools\server-stop.ps1') + '" -Remove -DisableSshd',
      '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and WizardIsComponentSelected('server') then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Other PCs can now connect to this one. In the SSH Manager app on another PC, add this PC with:' + #13#10 +
      '   PC name:  ' + GetComputerNameString + #13#10 +
      '   User name:  ' + GetUserNameString + #13#10#13#10 +
      'The first time, the app asks for this account''s Windows password once.';
end;
