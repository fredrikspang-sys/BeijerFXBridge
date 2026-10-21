; Beijer FX Bridge installer (Inno Setup 6). Build: ./build.sh
#define AppExe "BeijerFXBridgeNet.exe"

[Setup]
AppId={{7C3E1B52-9A4D-4F0B-8E21-5D6A3F9C2B17}
AppName=Beijer FX Bridge
AppVersion=1.1.0
DefaultDirName={autopf}\BeijerFXBridge
DefaultGroupName=Beijer FX Bridge
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=BeijerFXBridge-1.1.0-setup
SetupIconFile=..\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
WizardSmallImageFile=wizsmall-55.png,wizsmall-110.png

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; config.ini is saved next to the exe, so normal users need write access here
Name: "{app}"; Permissions: users-modify

[Files]
Source: "..\bin\Release\net48\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "config.ini"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "com0com\*"; DestDir: "{app}\com0com"
Source: "downloads\com0com-2.2.2.0-x64-fre-signed\setup.exe"; DestDir: "{tmp}"; DestName: "com0com-setup.exe"; Flags: deleteafterinstall; Check: NeedCom0com

[Icons]
Name: "{group}\Beijer FX Bridge"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall Beijer FX Bridge"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Beijer FX Bridge"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\com0com-setup.exe"; Parameters: "/S"; StatusMsg: "Installing com0com..."; Check: NeedCom0com
Filename: "{code:Com0comDir}\setupc.exe"; Parameters: "--silent install PortName=COM10 PortName=COM11"; WorkingDir: "{code:Com0comDir}"; StatusMsg: "Creating com0com pair COM10 <-> COM11..."; Flags: runhidden; Check: NeedPorts
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Beijer FX Bridge}"; Flags: postinstall nowait skipifsilent

; The uninstaller leaves com0com and its ports alone.

[Code]
const
  C0CParams = 'SYSTEM\CurrentControlSet\Services\com0com\Parameters';

function Com0comDir(Param: String): String;
var
  S: String;
begin
  Result := '';
  if FileExists(ExpandConstant('{commonpf64}\com0com\setupc.exe')) then
    Result := ExpandConstant('{commonpf64}\com0com')
  else if FileExists(ExpandConstant('{commonpf32}\com0com\setupc.exe')) then
    Result := ExpandConstant('{commonpf32}\com0com')
  else if RegQueryStringValue(HKLM32, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\com0com', 'UninstallString', S) then
  begin
    S := ExtractFileDir(RemoveQuotes(S));
    if FileExists(S + '\setupc.exe') then Result := S;
  end;
end;

function NeedCom0com: Boolean;
begin
  Result := not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\com0com') and (Com0comDir('') = '');
end;

{ True if Name is an active serial port or is already assigned to a com0com port }
function PortExists(Name: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  V: String;
begin
  Result := False;
  if RegGetValueNames(HKLM, 'HARDWARE\DEVICEMAP\SERIALCOMM', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(HKLM, 'HARDWARE\DEVICEMAP\SERIALCOMM', Names[I], V) and (CompareText(V, Name) = 0) then
        Result := True;
  if RegGetSubkeyNames(HKLM, C0CParams, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(HKLM, C0CParams + '\' + Names[I], 'PortName', V) and (CompareText(V, Name) = 0) then
        Result := True;
end;

function NeedPorts: Boolean;
begin
  Result := (Com0comDir('') <> '') and not PortExists('COM10') and not PortExists('COM11');
  if not Result then
    Log('Not creating COM10/COM11: com0com not found or a port name is already in use');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent and
     (RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\com0com') or (Com0comDir('') <> '')) then
    MsgBox('Beijer FX Bridge has been removed. com0com and its virtual COM ports (e.g. COM10/COM11) were left installed, ' +
      'because other programs may use them. If you no longer need them, remove "Null-modem emulator (com0com)" ' +
      'under Settings > Apps > Installed apps.', mbInformation, MB_OK);
end;

procedure InitializeWizard;
begin
  { item Offset (default 4 px) is not DPI-scaled; widen it so the task checkbox isn't clipped on high DPI }
  WizardForm.TasksList.Offset := WizardForm.TasksList.Offset + ScaleX(4);
end;
