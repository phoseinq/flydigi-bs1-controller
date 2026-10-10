; BS1 Cooler installer (English + Persian). Build: installer\build.ps1
#define AppName "BS1 Cooler"
#define AppExe "Bs1Cooler.exe"
#define AppVersion GetVersionNumbersString(AddBackslash(SourcePath) + "app\" + AppExe)
#define TaskName "BS1 Cooler"

[Setup]
AppId={{973ADAFE-356C-4128-9C7A-05B99D0FA03D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=phoseinq
DefaultDirName={autopf}\BS1Cooler
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=out
OutputBaseFilename=BS1Cooler-Setup-{#AppVersion}
SetupIconFile=..\cooler\cooler.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=yes
; preselect English (the first language) instead of following the Windows UI language
LanguageDetectionMethod=none
CloseApplications=force

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fa"; MessagesFile: "Farsi.isl"

[CustomMessages]
en.SpaceStation=Flydigi Space Station must not run at the same time (only one app can talk to the cooler). Setup will close it; remove it from startup if it starts with Windows.
fa.SpaceStation=برنامه‌ی Flydigi Space Station نباید هم‌زمان اجرا باشه (فقط یه برنامه می‌تونه به کولر وصل بشه). نصب‌کننده اون رو می‌بنده؛ اگه با ویندوز اجرا می‌شه، از Startup حذفش کنید.
en.StartNow=Start BS1 Cooler now
fa.StartNow=الان BS1 Cooler اجرا بشه

[Files]
Source: "app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"

[Run]
; logon task: the app needs admin (sensors), and a task starts it elevated without a UAC prompt every boot
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Register-ScheduledTask -TaskName '{#TaskName}' -Force -RunLevel Highest -Action (New-ScheduledTaskAction -Execute '{app}\{#AppExe}' -Argument '--tray') -Trigger (New-ScheduledTaskTrigger -AtLogOn -User '{username}') -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0) | Out-Null"""; Flags: runhidden waituntilterminated
Filename: "schtasks.exe"; Parameters: "/Run /TN ""{#TaskName}"""; Description: "{cm:StartNow}"; Flags: runhidden postinstall

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "Kill"
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""{#TaskName}"" /F"; Flags: runhidden; RunOnceId: "DelTask"

[Code]
procedure KillApps();
var Code: Integer;
begin
  Exec('taskkill.exe', '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec('taskkill.exe', '/F /IM FlydigiSpaceStation.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillApps();
  Result := '';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpReady then
    WizardForm.ReadyMemo.Lines.Add(#13#10 + CustomMessage('SpaceStation'));
end;
