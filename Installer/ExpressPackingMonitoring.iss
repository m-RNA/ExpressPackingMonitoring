#ifndef MyAppVersion
  #error MyAppVersion is required
#endif
#ifndef MyAppVersion4
  #error MyAppVersion4 is required
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

#define MyAppName "快递打包监控"
#define MyAppExeName "ExpressPackingMonitoring.exe"
#define MyAppId "{{99E9FCE3-C8FE-4D7A-9FA4-BC9CB9186B05}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher=m-RNA
AppPublisherURL=https://github.com/m-RNA/ExpressPackingMonitoring
AppSupportURL=https://github.com/m-RNA/ExpressPackingMonitoring/issues
AppUpdatesURL=https://github.com/m-RNA/ExpressPackingMonitoring/releases
DefaultDirName={localappdata}\Programs\ExpressPackingMonitoring
DefaultGroupName={#MyAppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=ExpressPackingMonitoring_Setup_v{#MyAppVersion}
SetupIconFile=..\ExpressPackingMonitoring\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
LicenseFile=..\LICENSE
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=ExpressPackingMonitoring.exe
RestartApplications=no
VersionInfoVersion={#MyAppVersion4}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DeleteLocalData: Boolean;
  RecordingCleanupFailed: Boolean;
  CleanupPlanPath: String;
  CleanupLogPath: String;

function Quote(const Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function IsSilentUninstall: Boolean;
var
  Index: Integer;
  Argument: String;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    Argument := Uppercase(ParamStr(Index));
    if (Argument = '/SILENT') or (Argument = '/VERYSILENT') then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function ExtractJsonInt64(const Json, PropertyName: String): Int64;
var
  Marker: String;
  MarkerPos: Integer;
  ValueStart: Integer;
  ValueEnd: Integer;
  ValueText: String;
begin
  Marker := '"' + PropertyName + '"';
  MarkerPos := Pos(Marker, Json);
  if MarkerPos = 0 then
    RaiseException('清理清单缺少字段：' + PropertyName);

  ValueStart := MarkerPos + Length(Marker);
  while (ValueStart <= Length(Json)) and (Json[ValueStart] <> ':') do
    ValueStart := ValueStart + 1;
  if ValueStart > Length(Json) then
    RaiseException('清理清单字段格式无效：' + PropertyName);

  ValueStart := ValueStart + 1;
  while (ValueStart <= Length(Json)) and
        ((Json[ValueStart] = ' ') or (Json[ValueStart] = #9) or
         (Json[ValueStart] = #13) or (Json[ValueStart] = #10)) do
    ValueStart := ValueStart + 1;

  ValueEnd := ValueStart;
  while (ValueEnd <= Length(Json)) and
        (Json[ValueEnd] >= '0') and (Json[ValueEnd] <= '9') do
    ValueEnd := ValueEnd + 1;
  ValueText := Copy(Json, ValueStart, ValueEnd - ValueStart);
  if ValueText = '' then
    RaiseException('清理清单字段不是有效数字：' + PropertyName);
  Result := StrToInt64(ValueText);
end;

function FormatByteCount(const ByteCount: Int64): String;
begin
  if ByteCount >= 1073741824 then
    Result := IntToStr(ByteCount div 1073741824) + ' GB'
  else if ByteCount >= 1048576 then
    Result := IntToStr(ByteCount div 1048576) + ' MB'
  else if ByteCount >= 1024 then
    Result := IntToStr(ByteCount div 1024) + ' KB'
  else
    Result := IntToStr(ByteCount) + ' 字节';
end;

function RunCleanupCommand(const OptionName, PlanPath: String): Boolean;
var
  ResultCode: Integer;
  AppExe: String;
  Parameters: String;
begin
  AppExe := ExpandConstant('{app}\app\ExpressPackingMonitoring.exe');
  Parameters := OptionName + ' ' + Quote(PlanPath) +
    ' --uninstall-log ' + Quote(CleanupLogPath);
  Result :=
    FileExists(AppExe) and
    Exec(AppExe, Parameters, ExpandConstant('{app}\app'), SW_HIDE,
      ewWaitUntilTerminated, ResultCode) and
    (ResultCode = 0);
end;

procedure PrepareRecordingCleanup;
var
  Json: AnsiString;
  FileCount: Int64;
  TotalBytes: Int64;
  ConfirmationText: String;
begin
  if IsSilentUninstall then
    Exit;

  if SuppressibleMsgBox(
       '是否同时删除数据库登记的录像原文件？' + #13#10 + #13#10 +
       '默认不会删除录像。选择“是”后会先统计文件数量和容量，并再次确认。' +
       '未被数据库登记的文件和目录不会删除。',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
    Exit;

  if not RunCleanupCommand('--uninstall-plan-recordings', CleanupPlanPath) then
  begin
    RecordingCleanupFailed := True;
    MsgBox(
      '无法读取录像数据库，录像文件和本机应用数据均已保留。' + #13#10 +
      '详情见：' + CleanupLogPath,
      mbError, MB_OK);
    Exit;
  end;

  if not LoadStringFromFile(CleanupPlanPath, Json) then
  begin
    RecordingCleanupFailed := True;
    MsgBox('无法读取录像清理清单，录像文件和本机应用数据均已保留。', mbError, MB_OK);
    Exit;
  end;

  try
    FileCount := ExtractJsonInt64(String(Json), 'TotalFiles');
    TotalBytes := ExtractJsonInt64(String(Json), 'TotalBytes');
  except
    RecordingCleanupFailed := True;
    MsgBox('录像清理清单格式无效，录像文件和本机应用数据均已保留。', mbError, MB_OK);
    Exit;
  end;

  if FileCount = 0 then
  begin
    MsgBox('数据库中没有找到仍然存在的录像文件。', mbInformation, MB_OK);
    Exit;
  end;

  ConfirmationText :=
    '即将永久删除 ' + IntToStr(FileCount) + ' 个录像文件，合计约 ' +
    FormatByteCount(TotalBytes) + '。' + #13#10 + #13#10 +
    '此操作不可撤销。是否确认删除？';
  if SuppressibleMsgBox(
       ConfirmationText,
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
    Exit;

  if not RunCleanupCommand('--uninstall-delete-recordings', CleanupPlanPath) then
  begin
    RecordingCleanupFailed := True;
    MsgBox(
      '部分录像未能安全删除。本机应用数据已保留，便于继续核对和重试。' +
      Chr(13) + Chr(10) + '详情见：' + CleanupLogPath,
      mbError, MB_OK);
  end;
end;

procedure PrepareLocalDataCleanup;
begin
  if IsSilentUninstall then
  begin
    DeleteLocalData := False;
    Exit;
  end;

  DeleteLocalData :=
    SuppressibleMsgBox(
      '是否删除本机应用数据？' + #13#10 + #13#10 +
      '这会删除配置、数据库、日志和缓存。默认保留这些数据。' + #13#10 +
      '如果不同时删除录像，录像原文件会保留，但历史索引将被删除。',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
end;

procedure DeleteLocalApplicationData;
var
  UserDataPath: String;
  ExpectedPath: String;
begin
  if not DeleteLocalData or RecordingCleanupFailed then
    Exit;

  UserDataPath := RemoveBackslashUnlessRoot(
    ExpandConstant('{localappdata}\ExpressPackingMonitoring'));
  ExpectedPath := RemoveBackslashUnlessRoot(
    AddBackslash(ExpandConstant('{localappdata}')) + 'ExpressPackingMonitoring');
  if CompareText(UserDataPath, ExpectedPath) <> 0 then
  begin
    MsgBox('本机应用数据路径校验失败，数据已保留。', mbError, MB_OK);
    Exit;
  end;

  if DirExists(UserDataPath) and not DelTree(UserDataPath, True, True, True) then
    MsgBox(
      '部分本机应用数据未能删除，请稍后手动检查：' + UserDataPath,
      mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    CleanupPlanPath := ExpandConstant('{tmp}\ExpressPackingMonitoring-uninstall-recordings.json');
    CleanupLogPath := ExpandConstant('{tmp}\ExpressPackingMonitoring-Uninstall.log');
    DeleteFile(CleanupPlanPath);
    RecordingCleanupFailed := False;
    PrepareLocalDataCleanup;
    PrepareRecordingCleanup;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    DeleteLocalApplicationData;
    DeleteFile(CleanupPlanPath);
  end;
end;
