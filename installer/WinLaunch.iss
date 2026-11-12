; WinLaunch 安装包（Inno Setup 6）
; 由 build.ps1 -Release 调用，传入：
;   /DMyAppVersion=1.0.0
;   /DMyAppExe=<发布目录>\WinLaunch.exe
;   /O<发布目录>
;
; 单用户安装（不需要管理员）：默认装到 %LOCALAPPDATA%\Programs\WinLaunch。
; 卸载时询问是否一并删除配置（%APPDATA%\WinLaunch）与缓存（%LOCALAPPDATA%\WinLaunch），
; 并清掉「开机自启」的 HKCU Run 项。

#define MyAppName "WinLaunch"
#define MyAppPublisher "yaqi"
#define MyAppExeName "WinLaunch.exe"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyAppExe
  #define MyAppExe "WinLaunch.exe"
#endif
#define MyDocFile AddBackslash(ExtractFilePath(MyAppExe)) + "使用说明.md"

[Setup]
AppId={{7F1B3B52-2C4E-4C2B-9E1F-5D6A8C0E4A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/
AppSupportURL=https://github.com/
DefaultDirName={localappdata}\Programs\WinLaunch
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=WinLaunch-{#MyAppVersion}-setup
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
CloseApplications=no
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion
#if FileExists(MyDocFile)
Source: "{#MyDocFile}"; DestDir: "{app}"; Flags: ignoreversion
#endif

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Registry]
; 卸载时清掉应用自己写入的开机自启项（安装时不写，交给应用里的开关）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "WinLaunch"; Flags: uninsdeletevalue

[Code]
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  { 安装前先结束正在运行的旧版本，否则 exe 被占用无法覆盖 }
  Exec(ExpandConstant('{cmd}'), '/c taskkill /IM WinLaunch.exe /F >nul 2>&1', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Answer: Integer;
  ForceDelete: Boolean;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { 只有显式传 /DELETEUSERDATA=1 才删数据；静默卸载(/SILENT)一律保留，
      避免脚本化卸载误删用户配置 }
    ForceDelete := ExpandConstant('{param:DELETEUSERDATA|0}') = '1';
    Answer := IDNO;
    if (not ForceDelete) and (not UninstallSilent) then
      Answer := MsgBox('是否同时删除配置、使用记录和图标缓存？'#13#10#13#10 +
                       '将删除：'#13#10 +
                       '  %APPDATA%\WinLaunch（配置 / 使用记录 / 日志）'#13#10 +
                       '  %LOCALAPPDATA%\WinLaunch（图标缓存）'#13#10#13#10 +
                       '选择「否」会保留这些数据，方便以后重新安装时继续用。',
                       mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
    if ForceDelete or (Answer = IDYES) then
    begin
      DelTree(ExpandConstant('{userappdata}\WinLaunch'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\WinLaunch'), True, True, True);
    end;
  end;
end;
