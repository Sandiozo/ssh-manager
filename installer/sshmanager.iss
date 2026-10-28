; SSH Manager installer (Inno Setup 6.5 or newer). Built by build.ps1, which first puts the app, the
; server, HiddenRun and the command-line tool into ..\build\{app,server,tools,cli}.
;  - "What should this PC do?" page: Client (the app, to manage other PCs) or Client and server (also
;    let other PCs manage this one). It shows what is installed now and which dependencies are already
;    there or will be downloaded.
;  - Downloads (and checks Microsoft's signature on) the .NET 10 Desktop Runtime and the WebView2
;    Runtime if the app needs them.
;  - The server role turns on Windows' OpenSSH Server and starts SSH Manager at logon (server-setup.ps1).
;  - The app comes with the command-line tool sshmgr.exe (in Cli, which is added to PATH).
;  - Also used for updates: run silently (/VERYSILENT) it keeps the roles chosen before, only stops
;    SSH Manager's own processes, reopens the app for everyone who had it open, and starts the server
;    again even when the update fails or is cancelled.
; Command line: /ROLES=app or /ROLES=app,server picks the roles for a new install; /SERVERUSER=<account>
; picks the administrator account the server runs as. Uninstall: /REMOVEACCESS also removes the SSH
; keys SSH Manager added and turns off OpenSSH Server (asked about when not silent).

#ifndef Version
  #define Version "0.0.0"
#endif
#define BuildDir "..\build"
#define UninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A51-6D4E-4B7A-9C1D-2E5F7A8B9C01}_is1"

#if Ver < EncodeVer(6, 5, 0)
  #error Inno Setup 6.5 or newer is needed (UTF-8 progress files, ExtractTemporaryFile of installed files)
#endif

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
WizardSmallImageFile=wizard-small-58.bmp,wizard-small-73.bmp,wizard-small-87.bmp,wizard-small-116.bmp
UninstallDisplayIcon={app}\App\SSHManager.exe
; tells Explorer icons changed, so shortcuts pick up a new app icon after an update
ChangesAssociations=yes
; tells running programs that PATH changed (the Cli folder with sshmgr.exe)
ChangesEnvironment=yes
UninstallDisplayName=SSH Manager
#ifndef PreviewOnly
LicenseFile=..\LICENSE
#endif
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; one setup at a time (also across sessions); ensure-running.ps1 waits while it exists
SetupMutex=SSHManagerSetup,Global\SSHManagerSetup
; Restart Manager would close any program using the files, and can't reach apps in other sessions.
; PrepareToInstall stops SSH Manager's own processes instead (server-stop.ps1), moves a busy sshmgr.exe
; aside, and the app is reopened at the end for every user who had it open (reopen-app.ps1).
CloseApplications=no
RestartApplications=no
; writes %TEMP%\Setup Log <date> #NNN.txt, for when an install doesn't finish
SetupLogging=yes

; What gets installed is decided only by the "What should this PC do?" page (WantApp / WantServer in
; [Code]), not by Inno's components, which would otherwise bring back the previous choice. Silent
; installs (updates) keep the roles already on the PC; /ROLES=app,server picks them for a new install.
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Check: WantApp

[Files]
; first, so PrepareToInstall can extract them quickly (solid compression)
Source: "server-stop.ps1"; DestDir: "{app}\Tools"; Flags: ignoreversion
Source: "reopen-app.ps1"; DestDir: "{app}\Tools"; Flags: ignoreversion
Source: "{#BuildDir}\app\*"; DestDir: "{app}\App"; Check: WantApp; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#BuildDir}\cli\sshmgr.exe"; DestDir: "{app}\Cli"; Check: WantApp; Flags: ignoreversion
Source: "{#BuildDir}\server\*"; DestDir: "{app}\Server"; Check: WantServer; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\ensure-running.ps1"; DestDir: "{app}\Server"; Check: WantServer; Flags: ignoreversion
Source: "..\restart-server.ps1"; DestDir: "{app}\Server"; Check: WantServer; Flags: ignoreversion
Source: "{#BuildDir}\tools\HiddenRun.exe"; DestDir: "{app}\Tools"; Check: WantServer; Flags: ignoreversion
Source: "server-setup.ps1"; DestDir: "{app}\Tools"; Check: WantServer; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SSH Manager"; Filename: "{app}\App\SSHManager.exe"; Check: WantApp
Name: "{autodesktop}\SSH Manager"; Filename: "{app}\App\SSHManager.exe"; Tasks: desktopicon

[Run]
; the server role is set up from [Code] (RunServerSetup) so its progress can be shown
; Windows keeps showing a cached icon for shortcuts and taskbar pins: refresh it for the user
Filename: "{sys}\ie4uinit.exe"; Parameters: "-show"; Check: WantApp; Flags: runhidden nowait runasoriginaluser
Filename: "{app}\App\SSHManager.exe"; Description: "Open SSH Manager"; Check: WantApp; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
Type: files; Name: "{app}\Server\setup.log"
Type: files; Name: "{app}\Cli\sshmgr.exe.*.old"

[Code]
const
  { RollForward=Major: the app runs on .NET 10 or newer }
  DotNetMajor = 10;
  DotNetUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2Key = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  DownloadPage: TDownloadWizardPage;
  RolePage: TWizardPage;
  DepsLabel: TNewStaticText;
  AppInstalled, ServerInstalled, RemoveApp: Boolean;
  AppChosen, ServerChosen: Boolean;   { what this run installs; see WantApp / WantServer }
  PrevDir, PrevVersion: String;
  { set by PrepareToInstall: who had the app open, whether the server was stopped, sshmgr.exe moved aside }
  AppUsers: TArrayOfString;
  AppWasRunning, ServerStopped, InstallDone: Boolean;
  MovedCli: String;
  ServerUser: String;   { the account the server task runs as (from server-setup.ps1) }

function InstallingUser(Param: String): String;
begin
  Result := GetEnv('USERDOMAIN') + '\' + GetUserNameString;
end;

{ ---- versions: "1.6.0", "v1.7.0-rc.1" (only the leading digits of each part count; a pre-release
  comes before the release with the same numbers) ---- }

function VersionPart(V: String; Index: Integer): Integer;
var
  I, P: Integer;
  Part: String;
begin
  V := Trim(V);
  if (V <> '') and ((V[1] = 'v') or (V[1] = 'V')) then Delete(V, 1, 1);
  Part := '';
  for I := 0 to Index do
  begin
    P := Pos('.', V);
    if P = 0 then
    begin
      Part := V;
      V := '';
    end
    else
    begin
      Part := Copy(V, 1, P - 1);
      V := Copy(V, P + 1, Length(V));
    end;
  end;
  P := 1;
  while (P <= Length(Part)) and (Part[P] >= '0') and (Part[P] <= '9') do P := P + 1;
  Result := StrToIntDef(Copy(Part, 1, P - 1), 0);
end;

function CompareVersions(const A, B: String): Integer;
var
  I: Integer;
begin
  Result := 0;
  for I := 0 to 2 do
    if Result = 0 then Result := VersionPart(A, I) - VersionPart(B, I);
  if (Result = 0) and ((Pos('-', A) > 0) <> (Pos('-', B) > 0)) then
    if Pos('-', A) > 0 then Result := -1 else Result := 1;
end;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then Result := True;
end;

{ PowerShell: runs a script file as a script block, so a Group Policy execution policy (AllSigned)
  can't block it, and the file is read as UTF-8 }
function PsQuote(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

function PowerShellExe: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function PsArgs(const Script, Args: String): String;
begin
  Result := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "& ([scriptblock]::Create((Get-Content -LiteralPath ' +
    PsQuote(Script) + ' -Raw -Encoding UTF8))) ' + Args + '"';
end;

function RunPs(const Script, Args: String; var Code: Integer): Boolean;
begin
  Result := Exec(PowerShellExe, PsArgs(Script, Args), '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('PowerShell ' + ExtractFileName(Script) + ' ' + Args + ': exit code ' + IntToStr(Code));
end;

{ ---- what is already on this PC ---- }

function FindPreviousDir: String;
begin
  if not RegQueryStringValue(HKLM, '{#UninstallKey}', 'InstallLocation', Result) then Result := '';
  if (Result <> '') and (Result[Length(Result)] <> '\') then Result := Result + '\';
end;

function TaskExists: Boolean;
var
  Code: Integer;
begin
  Result := Exec('schtasks.exe', '/query /tn "SSH Manager"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function MajorOf(const Name: String): Integer;
begin
  Result := VersionPart(Name, 0);
end;

{ .NET Desktop Runtime 10 or newer for x64 (on ARM64 Windows the x64 runtime lives in dotnet\x64) }
function HasDotNetDesktop: Boolean;
var
  Rec: TFindRec;
  Dir: String;
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if IsArm64 then
    Dir := ExpandConstant('{commonpf64}\dotnet\x64\shared\Microsoft.WindowsDesktop.App\')
  else
    Dir := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\');
  if FindFirst(Dir + '*', Rec) then
  try
    repeat
      if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and (MajorOf(Rec.Name) >= DotNetMajor) and
         FileExists(Dir + Rec.Name + '\Microsoft.WindowsDesktop.App.deps.json') then
        Result := True;
    until Result or not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
  { the runtime installers also register their versions here }
  if not Result and RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if MajorOf(Names[I]) >= DotNetMajor then Result := True;
end;

function HasWebView2: Boolean;
var
  V: String;
begin
  Result := (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\' + WebView2Key, 'pv', V) and (V <> '') and (V <> '0.0.0.0'))
    or (RegQueryStringValue(HKCU, 'Software\' + WebView2Key, 'pv', V) and (V <> '') and (V <> '0.0.0.0'));
end;

{ Windows' feature, or the Win32-OpenSSH MSI from GitHub (Program Files\OpenSSH) }
function HasOpenSshServer: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\OpenSSH\sshd.exe')) or FileExists(ExpandConstant('{commonpf64}\OpenSSH\sshd.exe'))
    or RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\sshd');
end;

{ ---- downgrades ---- }

function InitializeSetup: Boolean;
begin
  Result := True;
  PrevDir := FindPreviousDir;
  if not RegQueryStringValue(HKLM, '{#UninstallKey}', 'DisplayVersion', PrevVersion) then PrevVersion := '';
  if (PrevVersion <> '') and (CompareVersions('{#Version}', PrevVersion) < 0) then
  begin
    Log('Downgrade: ' + PrevVersion + ' is installed, this is {#Version}');
    if not WizardSilent then
      Result := MsgBox('SSH Manager ' + PrevVersion + ' is installed, and this installer has an older version ({#Version}).' + #13#10#13#10 +
        'Settings saved by the newer version may not work with the older one. Install the older version anyway?',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  end;
end;

{ ---- "What should this PC do?": Client, or Client and server ---- }

var
  ClientRadio, BothRadio: TNewRadioButton;

function WillHaveServer: Boolean;
begin
  Result := (BothRadio <> nil) and BothRadio.Checked;
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
  { choosing a button while the page is still being built fires this too }
  if (BothRadio = nil) or (DepsLabel = nil) then Exit;
  S := DepLine('.NET Desktop Runtime ' + IntToStr(DotNetMajor), HasDotNetDesktop, 'about 60 MB') + #13#10 +
       DepLine('WebView2 Runtime', HasWebView2, 'about 2 MB') + #13#10;
  if WillHaveServer then
    S := S + DepLine('Windows'' OpenSSH Server', HasOpenSshServer, 'from Windows Update; can take 5-15 minutes') + #13#10 +
      '   The server runs as an administrator account (the one signed in, if it is one).' + #13#10;
  DepsLabel.Caption := S;
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

function AddRadio(const Caption: String; Top: Integer): TNewRadioButton;
begin
  Result := TNewRadioButton.Create(RolePage);
  Result.Parent := RolePage.Surface;
  Result.Left := 0;
  Result.Top := Top;
  Result.Width := RolePage.SurfaceWidth;
  Result.Height := ScaleY(18);
  Result.Caption := Caption;
  Result.Font.Style := [fsBold];
  Result.OnClick := @RolesChanged;
end;

function InstalledNow(Yes: Boolean): String;
begin
  if Yes then Result := '   (installed now)' else Result := '';
end;

procedure InitializeWizard;
var
  Now, Roles: String;
  Top: Integer;
  T: TNewStaticText;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading what SSH Manager needs...', nil);

  AppInstalled := (PrevDir <> '') and FileExists(PrevDir + 'App\SSHManager.exe');
  { the server's files decide, not the logon task (a failed setup may not have created it) }
  ServerInstalled := (PrevDir <> '') and FileExists(PrevDir + 'Server\SSHManagerServer.exe');
  { silent installs (updates) keep what is there; /ROLES=app,server picks the roles for a new one }
  Roles := Lowercase(ExpandConstant('{param:ROLES|}'));
  if Roles <> '' then
  begin
    AppChosen := Pos('app', Roles) > 0;
    ServerChosen := Pos('server', Roles) > 0;
  end
  else if AppInstalled or ServerInstalled then
  begin
    AppChosen := AppInstalled;
    ServerChosen := ServerInstalled;
  end
  else
  begin
    AppChosen := True;
    ServerChosen := True;
  end;

  RolePage := CreateCustomPage(wpLicense, 'What should this PC do?',
    'You can change this later by running this installer again.');
  Top := 0;
  if ServerInstalled then Now := 'Client and server' else if AppInstalled then Now := 'Client' else Now := '';
  if Now <> '' then
  begin
    T := AddText('This PC is set up as: ' + Now + '. Keep that choice to just update it.', 0, 0, False);
    Top := T.Top + T.Height + ScaleY(14);
  end;
  ClientRadio := AddRadio('Client' + InstalledNow(AppInstalled and not ServerInstalled), Top);
  T := AddText('Use SSH Manager on this PC to see and manage your other PCs. Your other PCs can''t connect to this one.',
    ScaleX(18), ClientRadio.Top + ClientRadio.Height + ScaleY(2), False);
  Top := T.Top + T.Height + ScaleY(12);
  BothRadio := AddRadio('Client and server' + InstalledNow(ServerInstalled), Top);
  T := AddText('The same, and your other PCs can manage this one too: turns on Windows'' OpenSSH Server and runs SSH Manager in the background.',
    ScaleX(18), BothRadio.Top + BothRadio.Height + ScaleY(2), False);
  Top := T.Top + T.Height + ScaleY(18);
  AddText('Needed for this:', 0, Top, True);
  DepsLabel := AddText('', 0, Top + ScaleY(18), False);
  if ServerInstalled then BothRadio.Checked := True else ClientRadio.Checked := True;
  RolesChanged(nil);
end;

{ Check functions for [Files], [Icons], [Tasks] and [Run] }
function WantApp: Boolean;
begin
  Result := AppChosen;
end;

function WantServer: Boolean;
begin
  Result := ServerChosen;
end;


function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + 'This PC will:' + NewLine;
  if AppChosen then
    Result := Result + Space + 'use SSH Manager' + NewLine
  else if AppInstalled then
    Result := Result + Space + 'no longer have the SSH Manager app (it will be removed)' + NewLine;
  if ServerChosen then
    Result := Result + Space + 'let other PCs manage it' + NewLine
  else if ServerInstalled then
    Result := Result + Space + 'no longer let other PCs manage it (that will be removed)' + NewLine;
  Result := Result + NewLine + 'Will be downloaded:' + NewLine;
  if AppChosen and not HasDotNetDesktop then Result := Result + Space + '.NET Desktop Runtime ' + IntToStr(DotNetMajor) + NewLine;
  if AppChosen and not HasWebView2 then Result := Result + Space + 'WebView2 Runtime' + NewLine;
  if ServerChosen and not HasOpenSshServer then Result := Result + Space + 'Windows'' OpenSSH Server (5-15 minutes)' + NewLine;
  if not ((AppChosen and not (HasDotNetDesktop and HasWebView2)) or (ServerChosen and not HasOpenSshServer)) then
    Result := Result + Space + 'nothing (everything needed is already installed)' + NewLine;
  if MemoTasksInfo <> '' then Result := Result + NewLine + MemoTasksInfo;
end;

{ ---- installing the runtimes the app needs ---- }

{ A downloaded runtime installer must carry a valid Authenticode signature from Microsoft }
function SignedByMicrosoft(const FileName: String): Boolean;
var
  Code: Integer;
begin
  Result := Exec(PowerShellExe, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$s = Get-AuthenticodeSignature -LiteralPath ' +
    PsQuote(FileName) + '; if ($s.Status -eq ''Valid'' -and $s.SignerCertificate.Subject -match ''O=Microsoft Corporation,'') { exit 0 }; ' +
    'Write-Output ($s.Status.ToString() + '' '' + $s.SignerCertificate.Subject); exit 1"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  Log('Signature of ' + FileName + ': ' + IntToStr(Code));
end;

function RunInstaller(const Name, Params, What: String): Boolean;
var
  Code: Integer;
  FileName: String;
begin
  FileName := ExpandConstant('{tmp}\') + Name;
  DownloadPage.SetText('Checking ' + What + '...', '');
  if not SignedByMicrosoft(FileName) then
  begin
    DeleteFile(FileName);
    SuppressibleMsgBox('The downloaded ' + What + ' isn''t signed by Microsoft, so it wasn''t run. Install it yourself from Microsoft''s ' +
      'website; SSH Manager may not start until it is installed.', mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;
  DownloadPage.SetText('Installing ' + What + '...', 'This usually takes a minute.');
  DownloadPage.ProgressBar.Style := npbstMarquee;
  try
    Result := Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) and ((Code = 0) or (Code = 3010) or (Code = 1638));
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
begin
  Result := True;

  if (RolePage <> nil) and (CurPageID = RolePage.ID) then
  begin
    AppChosen := True;
    ServerChosen := WillHaveServer;
    RemoveApp := False;
    Exit;
  end;

  { download missing runtimes when the app is being installed }
  if (CurPageID <> wpReady) or not AppChosen then Exit;
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

{ ---- stopping SSH Manager before its files are replaced ---- }

procedure AddAppUser(const U: String);
var
  N: Integer;
begin
  N := GetArrayLength(AppUsers);
  SetArrayLength(AppUsers, N + 1);
  AppUsers[N] := U;
end;

{ A running sshmgr.exe (an agent's MCP server, a script) can't be replaced, but it can be renamed:
  move it aside so the new one can be copied; the old copy is deleted next time (or at uninstall). }
procedure MoveCliAside(const Dir: String);
var
  Rec: TFindRec;
  Code: Integer;
begin
  if FindFirst(Dir + 'sshmgr.exe.*.old', Rec) then
  try
    repeat
      DeleteFile(Dir + Rec.Name);
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
  if not FileExists(Dir + 'sshmgr.exe') then Exit;
  MovedCli := Dir + 'sshmgr.exe.' + GetDateTimeString('yyyymmddhhnnss', #0, #0) + '.old';
  if not RenameFile(Dir + 'sshmgr.exe', MovedCli) then
  begin
    Log('Could not move sshmgr.exe aside; ending sshmgr.exe processes');
    MovedCli := '';
    Exec('taskkill.exe', '/f /im sshmgr.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code, I: Integer;
  Dir, StateFile: String;
  Lines: TArrayOfString;
begin
  Result := '';
  ExtractTemporaryFile('server-stop.ps1');
  ExtractTemporaryFile('reopen-app.ps1');
  Dir := PrevDir;
  if Dir = '' then Dir := AddBackslash(WizardDirValue);
  StateFile := ExpandConstant('{tmp}\running.txt');
  SetArrayLength(AppUsers, 0);
  ServerStopped := ServerInstalled or TaskExists;
  { only SSH Manager's own processes (started from its folder) are stopped; who had the app open is
    written to StateFile so it can be reopened for each of them }
  if RunPs(ExpandConstant('{tmp}\server-stop.ps1'), '-InstallDir ' + PsQuote(RemoveBackslash(Dir)) + ' -StateFile ' + PsQuote(StateFile), Code)
     and (Code = 0) and LoadStringsFromFile(StateFile, Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
      if Lines[I] = 'server' then ServerStopped := True
      else if Trim(Lines[I]) <> '' then AddAppUser(Trim(Lines[I]));
    AppWasRunning := GetArrayLength(AppUsers) > 0;
  end
  else
  begin
    { PowerShell didn't work: stop them by name }
    Log('server-stop.ps1 failed; stopping by process name');
    Exec('schtasks.exe', '/end /tn "SSH Manager"', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec('taskkill.exe', '/f /im SSHManagerServer.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    AppWasRunning := Exec(ExpandConstant('{cmd}'), '/c tasklist /fi "imagename eq SSHManager.exe" | find /i "SSHManager.exe"',
      '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
    if AppWasRunning then Exec('taskkill.exe', '/f /im SSHManager.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
  if AppChosen then MoveCliAside(Dir + 'Cli\');
end;

{ Starts the app again (in the tray) for the users who had it open; with none recorded, for the user
  at the console. Uses the installed reopen-app.ps1 without waiting, or the extracted one (setup
  failed: the files may be the old ones) and waits, as setup's temp folder goes away when it ends. }
procedure ReopenApp(const AppExe: String; FromTmp: Boolean);
var
  Code, I: Integer;
  Users, Script: String;
begin
  if not FileExists(AppExe) then Exit;
  Users := '';
  for I := 0 to GetArrayLength(AppUsers) - 1 do
  begin
    if Users <> '' then Users := Users + ',';
    Users := Users + PsQuote(AppUsers[I]);
  end;
  if Users <> '' then Users := ' -Users ' + Users;
  if FromTmp then
  begin
    Script := ExpandConstant('{tmp}\reopen-app.ps1');
    RunPs(Script, '-Exe ' + PsQuote(AppExe) + Users, Code);
  end
  else
  begin
    Script := ExpandConstant('{app}\Tools\reopen-app.ps1');
    Exec(PowerShellExe, PsArgs(Script, '-Exe ' + PsQuote(AppExe) + Users), '', SW_HIDE, ewNoWait, Code);
  end;
end;

procedure StartServerTask;
var
  Code: Integer;
begin
  if TaskExists then Exec('schtasks.exe', '/run /tn "SSH Manager"', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ Setup failed or was cancelled after SSH Manager was stopped: put back what was running }
procedure DeinitializeSetup;
var
  Dir: String;
begin
  if InstallDone or not (ServerStopped or AppWasRunning or (MovedCli <> '')) then Exit;
  Log('Setup did not finish: starting SSH Manager again');
  Dir := PrevDir;
  if Dir = '' then Dir := AddBackslash(WizardDirValue);
  if (MovedCli <> '') and not FileExists(Dir + 'Cli\sshmgr.exe') then RenameFile(MovedCli, Dir + 'Cli\sshmgr.exe');
  if ServerStopped and FileExists(Dir + 'Server\SSHManagerServer.exe') then StartServerTask;
  if AppWasRunning then ReopenApp(Dir + 'App\SSHManager.exe', True);
end;

{ ---- the server role ---- }

{ Removing the server role or uninstalling: asks (or, silently, follows /REMOVEACCESS) whether to also
  remove the SSH keys SSH Manager added and turn off OpenSSH Server. Returns server-stop.ps1 switches. }
function AskRemoveAccess(const Why: String; Silent: Boolean): String;
begin
  Result := '';
  if HasParam('/REMOVEACCESS') then
  begin
    Result := ' -RemoveKeys -DisableSshd';
    Exit;
  end;
  if Silent then Exit;
  if MsgBox(Why + #13#10#13#10 + 'Also remove the SSH keys SSH Manager added on this PC? The SSH Manager app on your other PCs ' +
      'then can''t sign in to this PC any more. (Keys whose comment starts with "ssh-manager@"; a backup is kept.)',
      mbConfirmation, MB_YESNO) = IDYES then
    Result := Result + ' -RemoveKeys';
  if MsgBox('Also turn off Windows'' OpenSSH Server on this PC? Choose No if you use SSH to reach this PC for anything else.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    Result := Result + ' -DisableSshd';
end;

function RunServerStop(const Args: String): Boolean;
var
  Code: Integer;
begin
  Result := RunPs(ExpandConstant('{app}\Tools\server-stop.ps1'), '-InstallDir ' + PsQuote(ExpandConstant('{app}')) + Args, Code);
end;

function ReadTextFile(const FileName: String): String;
var
  S: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(FileName, S) then Result := Trim(String(S));
  if Length(Result) > 1500 then Result := Copy(Result, 1, 1500) + '...';
end;

{ Runs server-setup.ps1 and shows what it is doing: it appends "percent|text|detail", "WARN|message"
  and "USER|account" lines to a UTF-8 progress file and ends with DONE or ERROR|message. It runs
  through cmd, which adds EXIT when PowerShell ends, so a script that dies early (blocked by policy,
  crashed) is noticed at once and its error output shown. Installing OpenSSH Server can take many minutes. }
procedure RunServerSetup;
var
  Page: TOutputProgressWizardPage;
  ProgressFile, ErrFile, Line, Rest, Text, Detail, ErrorText, Warnings, Args: String;
  Lines: TArrayOfString;
  Code, Pct, P, Count, Quiet, I: Integer;
  Finished, Upgrade: Boolean;
begin
  ProgressFile := ExpandConstant('{tmp}\server-progress.txt');
  ErrFile := ExpandConstant('{tmp}\server-errors.txt');
  DeleteFile(ProgressFile);
  DeleteFile(ErrFile);
  { a silent update that keeps the role: only the logon task and the settings folder are redone;
    OpenSSH Server and the firewall stay as they are }
  Upgrade := WizardSilent and ServerInstalled;
  Args := '-InstallDir ' + PsQuote(ExpandConstant('{app}')) + ' -User ' + PsQuote(InstallingUser('')) +
    ' -ProgressFile ' + PsQuote(ProgressFile);
  if Upgrade then Args := Args + ' -Upgrade';
  if ExpandConstant('{param:SERVERUSER|}') <> '' then Args := Args + ' -ServerUser ' + PsQuote(ExpandConstant('{param:SERVERUSER|}'));
  Page := CreateOutputProgressPage('Setting up this PC so your other PCs can manage it',
    'Turning on Windows'' OpenSSH Server and starting SSH Manager in the background.');
  Page.SetText('Starting...', '');
  Page.SetProgress(0, 100);
  Page.Show;
  Finished := False;
  ErrorText := '';
  Warnings := '';
  Pct := 0;
  Count := 0;
  Quiet := 0;
  try
    if not Exec(ExpandConstant('{cmd}'), '/s /c ""' + PowerShellExe + '" ' + PsArgs(ExpandConstant('{app}\Tools\server-setup.ps1'), Args) +
        ' 2>"' + ErrFile + '" & echo EXIT>>"' + ProgressFile + '""', '', SW_HIDE, ewNoWait, Code) then
    begin
      ErrorText := 'Could not start the setup script: ' + SysErrorMessage(Code);
      Finished := True;
    end;
    { the script reports at least every few seconds; 5 minutes of silence means it hangs }
    while (not Finished) and (Quiet < 5 * 60 * 4) do
    begin
      Sleep(250);
      Quiet := Quiet + 1;
      if LoadStringsFromLockedFile(ProgressFile, Lines) and (GetArrayLength(Lines) > Count) then
      begin
        Quiet := 0;
        for I := Count to GetArrayLength(Lines) - 1 do
        begin
          Line := Trim(Lines[I]);
          if Finished then
            { after DONE / ERROR only EXIT follows }
          else if Line = 'DONE' then
            Finished := True
          else if Copy(Line, 1, 6) = 'ERROR|' then
          begin
            ErrorText := Copy(Line, 7, Length(Line));
            Finished := True;
          end
          else if Line = 'EXIT' then
          begin
            ErrorText := 'The setup script stopped before it finished.';
            if ReadTextFile(ErrFile) <> '' then ErrorText := ErrorText + #13#10#13#10 + ReadTextFile(ErrFile);
            Finished := True;
          end
          else if Copy(Line, 1, 5) = 'WARN|' then
          begin
            Warnings := Warnings + #13#10#13#10 + Copy(Line, 6, Length(Line));
            Log('Server setup warning: ' + Copy(Line, 6, Length(Line)));
          end
          else if Copy(Line, 1, 5) = 'USER|' then
            ServerUser := Copy(Line, 6, Length(Line))
          else
          begin
            P := Pos('|', Line);
            Pct := StrToIntDef(Copy(Line, 1, P - 1), Pct);
            Rest := Copy(Line, P + 1, Length(Line));
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
        Count := GetArrayLength(Lines);
      end;
      Page.SetProgress(Pct, 100);   { also keeps the window responsive }
    end;
  finally
    Page.Hide;
  end;
  if not Finished then ErrorText := 'The setup script stopped responding.';
  if ErrorText <> '' then
  begin
    Log('Server setup failed: ' + ErrorText);
    { an update must not leave the server stopped }
    StartServerTask;
    SuppressibleMsgBox('Setting up "Let other PCs manage this PC" didn''t finish:' + #13#10#13#10 + ErrorText + #13#10#13#10 +
      'Details are in ' + ExpandConstant('{app}\Server\setup.log') + '. Run this installer again to retry.', mbError, MB_OK, IDOK);
  end
  else if Warnings <> '' then
    SuppressibleMsgBox('This PC is set up, but:' + Warnings, mbInformation, MB_OK, IDOK);
end;

{ ---- the command-line tool: its folder on the system PATH ---- }

const
  EnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

procedure SetCliOnPath(Add: Boolean);
var
  Path, Dir, Rest, Item, Kept: String;
  P: Integer;
  Found: Boolean;
begin
  Dir := ExpandConstant('{app}\Cli');
  if not RegQueryStringValue(HKLM, EnvKey, 'Path', Path) then Path := '';
  Kept := '';
  Found := False;
  Rest := Path;
  while Rest <> '' do
  begin
    P := Pos(';', Rest);
    if P = 0 then P := Length(Rest) + 1;
    Item := Copy(Rest, 1, P - 1);
    Rest := Copy(Rest, P + 1, Length(Rest));
    if CompareText(RemoveBackslashUnlessRoot(Trim(Item)), Dir) = 0 then
      Found := True
    else if Trim(Item) <> '' then
    begin
      if Kept <> '' then Kept := Kept + ';';
      Kept := Kept + Item;
    end;
  end;
  if Add = Found then Exit;
  if Add then
  begin
    if Kept <> '' then Kept := Kept + ';';
    Kept := Kept + Dir;
  end;
  RegWriteExpandStringValue(HKLM, EnvKey, 'Path', Kept);
end;

{ "Start with Windows" entries of the app: the current user's, and those of other users whose
  registry is loaded (signed in). Entries of users who aren't signed in stay (they point to a
  missing file, which Windows skips). }
procedure RemoveRunEntries;
var
  Names: TArrayOfString;
  I: Integer;
  V: String;
begin
  RegDeleteValue(HKCU, RunKey, 'SSH Manager');
  if RegGetSubkeyNames(HKU, '', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if (Pos('_Classes', Names[I]) = 0) and ((Pos('S-1-5-21-', Names[I]) = 1) or (Pos('S-1-12-1-', Names[I]) = 1)) and
         RegQueryStringValue(HKU, Names[I] + '\' + RunKey, 'SSH Manager', V) and (Pos('sshmanager.exe', Lowercase(V)) > 0) then
        RegDeleteValue(HKU, Names[I] + '\' + RunKey, 'SSH Manager');
end;

{ After the files are in place: set up the server role, and remove roles that were unticked (the
  installer itself only adds files). }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then
  begin
    if AppChosen and AppWasRunning then ReopenApp(ExpandConstant('{app}\App\SSHManager.exe'), False);
    Exit;
  end;
  if CurStep <> ssPostInstall then Exit;
  InstallDone := True;
  if AppChosen then SetCliOnPath(True);
  if ServerChosen then
    RunServerSetup
  else if DirExists(ExpandConstant('{app}\Server')) then
  begin
    WizardForm.StatusLabel.Caption := 'Removing "Let other PCs manage this PC"...';
    RunServerStop(' -Remove' + AskRemoveAccess('Other PCs can no longer manage this PC.', WizardSilent));
    DelTree(ExpandConstant('{app}\Server'), True, True, True);
    DeleteFile(ExpandConstant('{app}\Tools\HiddenRun.exe'));
    DeleteFile(ExpandConstant('{app}\Tools\server-setup.ps1'));
  end;
  if RemoveApp then
  begin
    WizardForm.StatusLabel.Caption := 'Removing the SSH Manager app...';
    DelTree(ExpandConstant('{app}\App'), True, True, True);
    DelTree(ExpandConstant('{app}\Cli'), True, True, True);
    SetCliOnPath(False);
    DeleteFile(ExpandConstant('{autoprograms}\SSH Manager.lnk'));
    DeleteFile(ExpandConstant('{autodesktop}\SSH Manager.lnk'));
    RemoveRunEntries;
  end;
end;

{ Uninstall: stop everything of SSH Manager (also sshmgr.exe), remove the logon task, the settings
  folder in ProgramData and the firewall rule; ask about the SSH keys and OpenSSH Server. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Extra: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Extra := '';
    if DirExists(ExpandConstant('{app}\Server')) then
      Extra := AskRemoveAccess('Other PCs will no longer be able to manage this PC with SSH Manager.', UninstallSilent);
    RunServerStop(' -Remove -StopCli' + Extra);
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    SetCliOnPath(False);
    RemoveRunEntries;
  end;
end;

function ShortUser(const U: String): String;
begin
  Result := U;
  while Pos('\', Result) > 0 do Result := Copy(Result, Pos('\', Result) + 1, Length(Result));
end;

procedure CurPageChanged(CurPageID: Integer);
var
  U: String;
begin
  if (CurPageID = wpFinished) and ServerChosen then
  begin
    U := ShortUser(ServerUser);
    if U = '' then U := GetUserNameString;
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'Other PCs can now connect to this one. In the SSH Manager app on another PC, add this PC with:' + #13#10 +
      '   PC name:  ' + GetComputerNameString + #13#10 +
      '   User name:  ' + U + '  (or another administrator account of this PC)' + #13#10#13#10 +
      'The first time, the app asks for this account''s Windows password once.';
  end;
end;
