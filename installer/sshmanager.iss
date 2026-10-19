; SSH Manager installer (Inno Setup 6). Built by build.ps1, which first puts the app, the server and
; HiddenRun into ..\build\{app,server,tools}.
;  - "What should this PC do?" page: the app (manage other PCs) and/or the server (let other PCs manage
;    this one). Roles already installed can't be chosen again, only removed; the page lists which
;    dependencies are already there and which will be downloaded.
;  - Downloads and installs the .NET 8 Desktop Runtime and the WebView2 Runtime if the app needs them.
;  - The server role turns on Windows' OpenSSH Server and starts SSH Manager at logon (server-setup.ps1).
;  - Also used for updates: run silently (/VERYSILENT) it keeps the roles chosen before.

#ifndef Version
  #define Version "0.0.0"
#endif
#define BuildDir "..\build"
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A51-6D4E-4B7A-9C1D-2E5F7A8B9C01}_is1"

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
#ifdef PreviewOnly
; build with /DPreviewOnly to look at the wizard pages without admin rights (don't install with it)
PrivilegesRequired=lowest
OutputBaseFilename=SSHManager-Setup-preview
#else
PrivilegesRequired=admin
OutputBaseFilename=SSHManager-Setup
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#BuildDir}
SetupIconFile=..\app\SSHManager\app.ico
UninstallDisplayIcon={app}\App\SSHManager.exe
UninstallDisplayName=SSH Manager
#ifndef PreviewOnly
LicenseFile=..\LICENSE
#endif
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=yes
; writes %TEMP%\Setup Log <date> #NNN.txt, for when an install doesn't finish
SetupLogging=yes

; The roles are chosen on the custom "What should this PC do?" page; the standard components page is
; skipped. Types/components still decide which files are installed, and /COMPONENTS= works for
; scripted installs.
[Types]
Name: "both"; Description: "Both"
Name: "app"; Description: "App only"
Name: "server"; Description: "Managed only"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "app"; Description: "SSH Manager app"; Types: both app
Name: "server"; Description: "Let other PCs manage this PC"; Types: both server

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
; the server role is set up from [Code] (RunServerSetup) so its progress can be shown
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
  RolePage: TWizardPage;
  AppBox, ServerBox, RemoveAppBox, RemoveServerBox: TNewCheckBox;
  NowLabel, DepsLabel: TNewStaticText;
  AppInstalled, ServerInstalled, RemoveApp: Boolean;

function InstallingUser(Param: String): String;
begin
  Result := GetEnv('USERDOMAIN') + '\' + GetUserNameString;
end;

{ ---- what is already on this PC ---- }

function PreviousDir: String;
begin
  if not RegQueryStringValue(HKLM, '{#UninstallKey}', 'InstallLocation', Result) then Result := '';
  if (Result <> '') and (Result[Length(Result)] <> '\') then Result := Result + '\';
end;

function ServerTaskExists: Boolean;
var
  Code: Integer;
begin
  Result := Exec('schtasks.exe', '/query /tn "SSH Manager"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
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

function HasOpenSshServer: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\OpenSSH\sshd.exe'));
end;

{ ---- "What should this PC do?" ---- }

function WillHaveApp: Boolean;
begin
  if AppInstalled then Result := not RemoveAppBox.Checked else Result := AppBox.Checked;
end;

function WillHaveServer: Boolean;
begin
  if ServerInstalled then Result := not RemoveServerBox.Checked else Result := ServerBox.Checked;
end;

function DepLine(const Name: String; Present: Boolean; const Download: String): String;
begin
  if Present then
    Result := '   ' + Name + ': already installed (left as it is)'
  else
    Result := '   ' + Name + ': will be downloaded and installed (' + Download + ')';
end;

procedure RolesChanged(Sender: TObject);
var
  S: String;
begin
  { ticking a box while the page is still being built fires this too }
  if (RemoveServerBox = nil) or (DepsLabel = nil) then Exit;
  S := '';
  if WillHaveApp then
    S := S + DepLine('.NET Desktop Runtime 8', HasDotNetDesktop, 'about 55 MB') + #13#10 +
             DepLine('WebView2 Runtime', HasWebView2, 'about 2 MB') + #13#10;
  if WillHaveServer then
    S := S + DepLine('Windows'' OpenSSH Server', HasOpenSshServer, 'from Windows Update; can take 5-15 minutes') + #13#10;
  if S = '' then S := '   Nothing.';
  DepsLabel.Caption := S;
end;

function AddBox(const Caption: String; Left, Top: Integer): TNewCheckBox;
begin
  Result := TNewCheckBox.Create(RolePage);
  Result.Parent := RolePage.Surface;
  Result.Left := Left;
  Result.Top := Top;
  Result.Width := RolePage.SurfaceWidth - Left;
  Result.Height := ScaleY(18);
  Result.Caption := Caption;
  Result.OnClick := @RolesChanged;
end;

function AddText(const Caption: String; Left, Top: Integer; Bold: Boolean): TNewStaticText;
begin
  Result := TNewStaticText.Create(RolePage);
  Result.Parent := RolePage.Surface;
  Result.Left := Left;
  Result.Top := Top;
  Result.Width := RolePage.SurfaceWidth - Left;
  Result.WordWrap := True;
  Result.AutoSize := True;
  Result.Caption := Caption;
  if Bold then Result.Font.Style := [fsBold];
end;

var
  NewBox, NewRemoveBox: TNewCheckBox;   { set by AddRole }

{ One role: a checkbox to add it, or, when it's already installed, a locked "installed" line and a
  "Remove" checkbox. Leaves them in NewBox / NewRemoveBox and returns the next free Top. }
function AddRole(const Name, Description: String; Installed: Boolean; Top: Integer): Integer;
var
  T: TNewStaticText;
begin
  if Installed then
  begin
    NewBox := AddBox(Name + ' (installed)', 0, Top);
    NewBox.Checked := True;
    NewBox.Enabled := False;
  end
  else
    NewBox := AddBox(Name, 0, Top);
  T := AddText(Description, ScaleX(18), NewBox.Top + NewBox.Height, False);
  Result := T.Top + T.Height + ScaleY(4);
  NewRemoveBox := AddBox('Remove it from this PC', ScaleX(18), Result);
  NewRemoveBox.Visible := Installed;
  if Installed then Result := NewRemoveBox.Top + NewRemoveBox.Height;
  Result := Result + ScaleY(12);
end;

procedure InitializeWizard;
var
  Dir, Now: String;
  Top: Integer;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading what SSH Manager needs...', nil);

  Dir := PreviousDir;
  AppInstalled := (Dir <> '') and FileExists(Dir + 'App\SSHManager.exe');
  ServerInstalled := (Dir <> '') and FileExists(Dir + 'Server\SSHManagerServer.exe') and ServerTaskExists;

  RolePage := CreateCustomPage(wpLicense, 'What should this PC do?',
    'You can change this later by running this installer again.');
  Now := '';
  if AppInstalled then Now := 'the app';
  if ServerInstalled then
  begin
    if Now <> '' then Now := Now + ' and ';
    Now := Now + 'letting other PCs manage it';
  end;
  if Now = '' then Now := 'nothing yet';
  NowLabel := AddText('Installed on this PC now: ' + Now + '.', 0, 0, False);
  Top := NowLabel.Top + NowLabel.Height + ScaleY(12);
  Top := AddRole('Use SSH Manager on this PC', 'See and manage your other PCs from this one.', AppInstalled, Top);
  AppBox := NewBox;
  RemoveAppBox := NewRemoveBox;
  Top := AddRole('Let other PCs manage this PC',
    'Turns on Windows'' OpenSSH Server and runs SSH Manager in the background, so your other PCs can connect to this one.',
    ServerInstalled, Top);
  ServerBox := NewBox;
  RemoveServerBox := NewRemoveBox;
  AddText('Needed for this, and what happens with it:', 0, Top, True);
  DepsLabel := AddText('', 0, Top + ScaleY(18), False);
  RolesChanged(nil);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := PageID = wpSelectComponents;   { replaced by the "What should this PC do?" page }
end;

{ ---- installing the runtimes the app needs ---- }

function RunInstaller(const Name, Params, What: String): Boolean;
var
  Code: Integer;
begin
  DownloadPage.SetText('Installing ' + What + '...', 'This usually takes a minute.');
  DownloadPage.ProgressBar.Style := npbstMarquee;
  try
    Result := Exec(ExpandConstant('{tmp}\') + Name, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) and ((Code = 0) or (Code = 3010) or (Code = 1638));
  finally
    DownloadPage.ProgressBar.Style := npbstNormal;
  end;
  if not Result then
    SuppressibleMsgBox('Installing ' + What + ' failed (code ' + IntToStr(Code) + '). SSH Manager may not start until it is installed.',
      mbError, MB_OK, IDOK);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  NeedDotNet, NeedWebView: Boolean;
  Sel: String;
begin
  Result := True;

  if (RolePage <> nil) and (CurPageID = RolePage.ID) then
  begin
    if not (WillHaveApp or WillHaveServer) then
    begin
      if AppInstalled or ServerInstalled then
        MsgBox('To remove SSH Manager completely, uninstall it in Settings > Apps.', mbInformation, MB_OK)
      else
        MsgBox('Choose at least one option.', mbInformation, MB_OK);
      Result := False;
      Exit;
    end;
    if WillHaveApp then Sel := 'app' else Sel := '!app';
    if WillHaveServer then Sel := Sel + ',server' else Sel := Sel + ',!server';
    WizardSelectComponents(Sel);
    RemoveApp := AppInstalled and not WillHaveApp;
    Exit;
  end;

  { download missing runtimes when the app is being installed }
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

{ Stop a running server before its files are replaced (the app is closed by Restart Manager, or here
  when it's being removed). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec('schtasks.exe', '/end /tn "SSH Manager"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec('taskkill.exe', '/f /im SSHManagerServer.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if RemoveApp then Exec('taskkill.exe', '/f /im SSHManager.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

{ ---- the server role ---- }

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

{ Runs server-setup.ps1 and shows what it is doing: it appends "percent|text|detail" lines to a
  progress file and ends with DONE or ERROR|message. Installing OpenSSH Server can take many minutes. }
procedure RunServerSetup;
var
  Page: TOutputProgressWizardPage;
  ProgressFile, Last, Rest, Text, Detail, ErrorText: String;
  Lines: TArrayOfString;
  Code, Pct, P, Count, Quiet: Integer;
  Finished: Boolean;
begin
  ProgressFile := ExpandConstant('{tmp}\server-progress.txt');
  DeleteFile(ProgressFile);
  Page := CreateOutputProgressPage('Setting up this PC so your other PCs can manage it',
    'Turning on Windows'' OpenSSH Server and starting SSH Manager in the background.');
  Page.SetText('Starting...', '');
  Page.SetProgress(0, 100);
  Page.Show;
  Finished := False;
  ErrorText := '';
  Pct := 0;
  Count := 0;
  Quiet := 0;
  try
    if not Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Tools\server-setup.ps1') +
        '" -InstallDir "' + ExpandConstant('{app}') + '" -User "' + InstallingUser('') + '" -ProgressFile "' + ProgressFile + '"',
        '', SW_HIDE, ewNoWait, Code) then
    begin
      ErrorText := 'Could not start the setup script: ' + SysErrorMessage(Code);
      Finished := True;
    end;
    { the script reports at least every few seconds; 5 minutes of silence means it died }
    while (not Finished) and (Quiet < 5 * 60 * 4) do
    begin
      Sleep(250);
      Quiet := Quiet + 1;
      if LoadStringsFromFile(ProgressFile, Lines) and (GetArrayLength(Lines) > Count) then
      begin
        Count := GetArrayLength(Lines);
        Quiet := 0;
        Last := Lines[Count - 1];
        if Last = 'DONE' then
          Finished := True
        else if Copy(Last, 1, 6) = 'ERROR|' then
        begin
          ErrorText := Copy(Last, 7, Length(Last));
          Finished := True;
        end
        else
        begin
          P := Pos('|', Last);
          Pct := StrToIntDef(Copy(Last, 1, P - 1), Pct);
          Rest := Copy(Last, P + 1, Length(Last));
          P := Pos('|', Rest);
          if P > 0 then
          begin
            Text := Copy(Rest, 1, P - 1);
            Detail := Copy(Rest, P + 1, Length(Rest));
          end
          else
          begin
            Text := Rest;
            Detail := '';
          end;
          Page.SetText(Text + '...', Detail);
        end;
      end;
      Page.SetProgress(Pct, 100);   { also keeps the window responsive }
    end;
  finally
    Page.Hide;
  end;
  if not Finished then ErrorText := 'The setup script stopped responding.';
  if ErrorText <> '' then
    SuppressibleMsgBox('Setting up "Let other PCs manage this PC" didn''t finish:' + #13#10#13#10 + ErrorText + #13#10#13#10 +
      'Details are in ' + ExpandConstant('{app}\Server\setup.log') + '. Run this installer again to retry.', mbError, MB_OK, IDOK);
end;

{ After the files are in place: set up the server role, and remove roles that were unticked (the
  installer itself only adds files). }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then Exit;
  if WizardIsComponentSelected('server') then
    RunServerSetup
  else if DirExists(ExpandConstant('{app}\Server')) then
  begin
    WizardForm.StatusLabel.Caption := 'Removing "Let other PCs manage this PC"...';
    RunServerStop(' -Remove' + AskDisableSshd('Other PCs can no longer manage this PC.'));
    DelTree(ExpandConstant('{app}\Server'), True, True, True);
    DeleteFile(ExpandConstant('{app}\Tools\HiddenRun.exe'));
    DeleteFile(ExpandConstant('{app}\Tools\server-setup.ps1'));
  end;
  if RemoveApp then
  begin
    WizardForm.StatusLabel.Caption := 'Removing the SSH Manager app...';
    DelTree(ExpandConstant('{app}\App'), True, True, True);
    DeleteFile(ExpandConstant('{autoprograms}\SSH Manager.lnk'));
    DeleteFile(ExpandConstant('{autodesktop}\SSH Manager.lnk'));
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SSH Manager');
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
