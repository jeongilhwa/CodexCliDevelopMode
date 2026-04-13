; ==========================================================================
; CodexDesk Installer - Inno Setup Script
; Installs: CodexDesk MAUI app + Node.js (MSI) + WebView2 + codex CLI
; ==========================================================================

#define MyAppName      "CodexDesk"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "CodexDesk"
#define MyAppExeName   "CodexDesk.exe"
#define MyAppUrl       "https://github.com/user/codexdesk"

[Setup]
AppId={{B3F7A1D2-9C4E-4F6B-8A2D-1E5C7D9F0B3A}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=..\artifacts\installer
OutputBaseFilename=CodexDeskSetup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
; SetupIconFile: Add a .ico file to installer\redist\ if you want a custom setup icon
MinVersion=10.0.17763

[Languages]
Name: "korean";  MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; CodexDesk app (published output)
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

; Node.js MSI (bundled for chain install)
Source: "redist\node-v22.*.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsNodeJsInstalled

; WebView2 bootstrapper
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsWebView2Installed

[Icons]
Name: "{group}\{#MyAppName}";    Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Step 0: Seed completed-chats.json into LocalAppData for the installed app
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""$src = '{app}\completed-chats.json'; $dstDir = Join-Path $env:LOCALAPPDATA 'CodexDesk'; $dst = Join-Path $dstDir 'completed-chats.json'; New-Item -ItemType Directory -Force -Path $dstDir | Out-Null; if ((Test-Path -LiteralPath $src) -and ((-not (Test-Path -LiteralPath $dst)) -or ((Get-Item -LiteralPath $dst).Length -lt 1024))) {{ Copy-Item -LiteralPath $src -Destination $dst -Force }"""; \
  StatusMsg: "Seeding completed chats baseline..."; \
  Flags: waituntilterminated runhidden

; Step 0-1: Seed completed-chat-threads.jsonl into LocalAppData for the installed app
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""$src = '{app}\completed-chat-threads.jsonl'; $dstDir = Join-Path $env:LOCALAPPDATA 'CodexDesk'; $dst = Join-Path $dstDir 'completed-chat-threads.jsonl'; New-Item -ItemType Directory -Force -Path $dstDir | Out-Null; if ((Test-Path -LiteralPath $src) -and ((-not (Test-Path -LiteralPath $dst)) -or ((Get-Item -LiteralPath $dst).Length -lt 1024))) {{ Copy-Item -LiteralPath $src -Destination $dst -Force }"""; \
  StatusMsg: "Seeding completed chat threads..."; \
  Flags: waituntilterminated runhidden

; Step 0-2: Seed completed-chats.pending.json into LocalAppData for the installed app
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command ""$src = '{app}\completed-chats.pending.json'; $dstDir = Join-Path $env:LOCALAPPDATA 'CodexDesk'; $dst = Join-Path $dstDir 'completed-chats.pending.json'; New-Item -ItemType Directory -Force -Path $dstDir | Out-Null; if ((Test-Path -LiteralPath $src) -and ((-not (Test-Path -LiteralPath $dst)) -or ((Get-Item -LiteralPath $dst).Length -lt 64))) {{ Copy-Item -LiteralPath $src -Destination $dst -Force }"""; \
  StatusMsg: "Seeding pending completed chats..."; \
  Flags: waituntilterminated runhidden

; Step 1: Install WebView2 if missing
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; \
  Parameters: "/silent /install"; \
  StatusMsg: "Installing Microsoft WebView2 Runtime..."; \
  Flags: waituntilterminated; \
  Check: not IsWebView2Installed

; Step 2: Install Node.js MSI if missing
Filename: "msiexec.exe"; \
  Parameters: "/i ""{tmp}\{code:GetNodeMsiFileName}"" /qn"; \
  StatusMsg: "Installing Node.js..."; \
  Flags: waituntilterminated shellexec; \
  Check: not IsNodeJsInstalled

; Step 3: Install codex CLI via npm (after Node.js is available)
Filename: "cmd.exe"; \
  Parameters: "/c npm install -g @openai/codex"; \
  StatusMsg: "Installing Codex CLI..."; \
  Flags: waituntilterminated runhidden; \
  Check: not IsCodexInstalled

; Step 4: Launch the app
Filename: "{app}\{#MyAppExeName}"; \
  Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
; npm uninstall codex on uninstall (optional, best-effort)
Filename: "cmd.exe"; \
  Parameters: "/c npm uninstall -g @openai/codex"; \
  Flags: runhidden waituntilterminated; \
  RunOnceId: "UninstallCodex"

[Code]
// -----------------------------------------------------------------------
// Helper: Check if WebView2 runtime is installed
// -----------------------------------------------------------------------
function IsWebView2Installed: Boolean;
var
  RegValue: String;
begin
  Result := RegQueryStringValue(HKLM64,
    'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
    'pv', RegValue);
  if Result then
    Result := (RegValue <> '') and (RegValue <> '0.0.0.0');
end;

// -----------------------------------------------------------------------
// Helper: Check if Node.js is installed (look for node.exe in PATH)
// -----------------------------------------------------------------------
function IsNodeJsInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/c node --version', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

// -----------------------------------------------------------------------
// Helper: Check if codex CLI is installed
// -----------------------------------------------------------------------
function IsCodexInstalled: Boolean;
var
  AppDataPath: String;
  CodexShim: String;
begin
  AppDataPath := ExpandConstant('{userappdata}');
  CodexShim := AppDataPath + '\npm\codex.cmd';
  Result := FileExists(CodexShim);
end;

// -----------------------------------------------------------------------
// Helper: Get the Node.js MSI filename from redist folder
// -----------------------------------------------------------------------
function GetNodeMsiFileName(Param: String): String;
var
  FindRec: TFindRec;
begin
  Result := '';
  if FindFirst(ExpandConstant('{tmp}\node-v22.*.msi'), FindRec) then
  begin
    Result := FindRec.Name;
    FindClose(FindRec);
  end;
end;

// -----------------------------------------------------------------------
// Inform user about what will be installed
// -----------------------------------------------------------------------
function InitializeSetup: Boolean;
var
  Msg: String;
begin
  if WizardSilent then
  begin
    Result := True;
    exit;
  end;

  Msg := '{#MyAppName} will install the following components:' + #13#10 + #13#10;

  Msg := Msg + '  - CodexDesk application' + #13#10;

  if not IsWebView2Installed then
    Msg := Msg + '  - Microsoft WebView2 Runtime (required)' + #13#10
  else
    Msg := Msg + '  - Microsoft WebView2 Runtime (already installed)' + #13#10;

  if not IsNodeJsInstalled then
    Msg := Msg + '  - Node.js runtime (required for Codex CLI)' + #13#10
  else
    Msg := Msg + '  - Node.js runtime (already installed)' + #13#10;

  if not IsCodexInstalled then
    Msg := Msg + '  - OpenAI Codex CLI (npm package)' + #13#10
  else
    Msg := Msg + '  - OpenAI Codex CLI (already installed)' + #13#10;

  Msg := Msg + #13#10 + 'Continue with installation?';

  Result := MsgBox(Msg, mbConfirmation, MB_YESNO) = IDYES;
end;
