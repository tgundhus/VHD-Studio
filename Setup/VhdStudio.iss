; VHD Studio setup (Inno Setup 7.1+). Setup\Publish.ps1 stages production binaries in ..\Publish\VhdStudio.

; Older compilers retain the 5.1.0 uninstaller and its old Pascal cleanup code on upgrade.
#if VER < EncodeVer(7,1,0)
  #error Inno Setup 7.1.0 or newer is required to update the installed uninstaller safely.
#endif

#define AppExe         "VhdStudio.exe"
#define ServiceExe     "VhdStudioService.exe"
#define AppName        GetStringFileInfo('..\Publish\VhdStudio\' + AppExe, 'ProductName')
#define AppVersion     GetStringFileInfo('..\Publish\VhdStudio\' + AppExe, 'ProductVersion')
#define AppFileVersion GetStringFileInfo('..\Publish\VhdStudio\' + AppExe, 'FileVersion')
#define AppCompany     GetStringFileInfo('..\Publish\VhdStudio\' + AppExe, 'CompanyName')
#define AppCopyright   GetStringFileInfo('..\Publish\VhdStudio\' + AppExe, 'LegalCopyright')
#define AppBase        LowerCase(StringChange(AppName, ' ', ''))
; /DSelfContained builds the standalone installer (.NET runtime embedded, no prerequisite).
#ifdef SelfContained
#  define AppSetupFile   AppBase + '-' + AppVersion + '-setup-standalone'
#else
#  define AppSetupFile   AppBase + '-' + AppVersion + '-setup'
#endif
#define AppUrl         "https://github.com/tgundhus/VHD-Studio"

#define AppVersionEx   AppVersion
#ifdef VersionHash
#  if "" != VersionHash
#    define AppVersionEx AppVersionEx + " (" + VersionHash + ")"
#  endif
#endif


[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppCompany}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright={#AppCopyright}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersionEx}
VersionInfoVersion={#AppFileVersion}
DefaultDirName={autopf}\{#AppName}
OutputBaseFilename={#AppSetupFile}
OutputDir=..\..\Releases
SourceDir=..\Publish\VhdStudio
AppId=VhdStudio
CloseApplications=yes
RestartApplications=no
AppMutex=Global\VhdStudio
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\..\Source\VhdAttach\Properties\App.ico
WizardStyle=modern
AlwaysShowComponentsList=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
MergeDuplicateFiles=yes
MinVersion=10.0.17763
PrivilegesRequired=admin
ShowLanguageDialog=no
SolidCompression=yes
ChangesAssociations=yes
DisableWelcomePage=yes
LicenseFile=..\..\Setup\License.rtf
#ifdef SignFile
SignTool=VhdStudioSignFile
SignedUninstaller=yes
#endif


[Messages]
SetupAppTitle=Setup {#AppName} {#AppVersionEx}
SetupWindowTitle=Setup {#AppName} {#AppVersionEx}
BeveledLabel={#AppName}


[Files]
Source: "*";                     DestDir: "{app}";  Excludes: "*.pdb";        Flags: ignoreversion recursesubdirs;
Source: "Recovery\VsuRecover.exe"; DestDir: "{app}\Recovery"; Flags: ignoreversion;
Source: "..\..\README.md";       DestDir: "{app}";  DestName: "ReadMe.txt";   Flags: overwritereadonly uninsremovereadonly;  Attribs: readonly;
Source: "..\..\LICENSE.md";      DestDir: "{app}";  DestName: "License.txt";  Flags: overwritereadonly uninsremovereadonly;  Attribs: readonly;


[Tasks]
Name: context_vhd_open;            GroupDescription: "VHD/VHDX context menu items:";  Description: "Open";
Name: context_vhd_attach;          GroupDescription: "VHD/VHDX context menu items:";  Description: "Attach";
Name: context_vhd_attachreadonly;  GroupDescription: "VHD/VHDX context menu items:";  Description: "Attach read-only";  Flags: unchecked;
Name: context_vhd_detach;          GroupDescription: "VHD/VHDX context menu items:";  Description: "Detach";
Name: context_vhd_maintain;        GroupDescription: "VHD/VHDX context menu items:";  Description: "Maintenance (compact, resize, convert...)";
Name: context_iso_open;            GroupDescription: "ISO context menu items:";       Description: "Open";
Name: context_iso_attachreadonly;  GroupDescription: "ISO context menu items:";       Description: "Attach";
Name: context_iso_detach;          GroupDescription: "ISO context menu items:";       Description: "Detach";


[Icons]
Name: "{autoprograms}\{#AppName}";               Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\{#AppName} Disk Manager";  Filename: "{app}\{#AppExe}";  Parameters: "/DiskManager";  Comment: "DiskPart-style partition tools"


[Registry]
; Only the marker is removed on uninstall; the auto-mount list survives so a reinstall keeps it.
Root: HKLM;  Subkey: "Software\xGND Software\VHD Studio";                      ValueType: dword;   ValueName: "Installed";         ValueData: "1";              Flags: uninsdeletevalue;
Root: HKLM;  Subkey: "Software\xGND Software";                                 ValueType: none;                                                                 Flags: uninsdeletekeyifempty;

; VSUX belongs to VHD Studio, not the Windows VHD handler. Do not replace another app's default.
Root: HKCR; Subkey: ".vsux"; ValueType: string; ValueName: ""; ValueData: "VhdStudio.Vsux"; Flags: createvalueifdoesntexist uninsdeletekeyifempty;
Root: HKCR; Subkey: ".vsux\OpenWithProgids"; ValueType: string; ValueName: "VhdStudio.Vsux"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty;
Root: HKCR; Subkey: "VhdStudio.Vsux"; ValueType: string; ValueName: ""; ValueData: "VHD Studio password-gated disk"; Flags: uninsdeletekey;
Root: HKCR; Subkey: "VhdStudio.Vsux\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0";
Root: HKCR; Subkey: "VhdStudio.Vsux\shell"; ValueType: string; ValueName: ""; ValueData: "open";
Root: HKCR; Subkey: "VhdStudio.Vsux\shell\open"; ValueType: string; ValueName: ""; ValueData: "Open with {#AppName}";
Root: HKCR; Subkey: "VhdStudio.Vsux\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1""";
Root: HKCR; Subkey: "VhdStudio.Vsux\shell\attach"; ValueType: string; ValueName: ""; ValueData: "Attach...";
Root: HKCR; Subkey: "VhdStudio.Vsux\shell\attach\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" /attach ""%1""";
Root: HKCR; Subkey: "VhdStudio.Vsux\shell\attachreadonly"; ValueType: string; ValueName: ""; ValueData: "Attach (read-only)...";
Root: HKCR; Subkey: "VhdStudio.Vsux\shell\attachreadonly\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" /readonly /attach ""%1""";
Root: HKCR; Subkey: "VhdStudio.Vsux\shell\detach"; ValueType: string; ValueName: ""; ValueData: "Detach and lock...";
Root: HKCR; Subkey: "VhdStudio.Vsux\shell\detach\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" /detach ""%1""";

; Make the built-in Windows.VhdFile/Windows.IsoFile handlers own the extensions so the verbs below show up.
Root: HKCR;  Subkey: ".vhd";                                                   ValueType: none;    ValueName: "";                  Flags: deletevalue;                                                     Tasks: context_vhd_open context_vhd_attach context_vhd_attachreadonly context_vhd_detach context_vhd_maintain;
Root: HKCR;  Subkey: ".vhd\OpenWithProgids";                                   ValueType: string;  ValueName: "Windows.VhdFile";   ValueData: "";                                                          Tasks: context_vhd_open context_vhd_attach context_vhd_attachreadonly context_vhd_detach context_vhd_maintain;
Root: HKCR;  Subkey: ".vhdx";                                                  ValueType: none;    ValueName: "";                  Flags: deletevalue;                                                     Tasks: context_vhd_open context_vhd_attach context_vhd_attachreadonly context_vhd_detach context_vhd_maintain;
Root: HKCR;  Subkey: ".vhdx\OpenWithProgids";                                  ValueType: string;  ValueName: "Windows.VhdFile";   ValueData: "";                                                          Tasks: context_vhd_open context_vhd_attach context_vhd_attachreadonly context_vhd_detach context_vhd_maintain;

; The default verb is removed again on uninstall so other tools are not left pointing at a missing verb (upstream issue #5).
Root: HKCR;  Subkey: "Windows.VhdFile\shell";                                  ValueType: string;  ValueName: "";                  ValueData: "VhdAttach-Open";        Flags: uninsdeletevalue;            Tasks: context_vhd_open;

Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Open";                   ValueType: none;    Flags: deletekey uninsdeletekey;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Open";                   ValueType: string;  ValueName: "";                  ValueData: "Open with {#AppName}";                                      Tasks: context_vhd_open;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Open";                   ValueType: string;  ValueName: "Icon";              ValueData: """{app}\{#AppExe}""";                                      Tasks: context_vhd_open;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Open";                   ValueType: string;  ValueName: "MultiSelectModel";  ValueData: "Document";                                                  Tasks: context_vhd_open;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Open\command";           ValueType: string;  ValueName: "";                  ValueData: """{app}\{#AppExe}"" ""%1""";                                Tasks: context_vhd_open;

Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Attach";                 ValueType: none;    Flags: deletekey uninsdeletekey;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Attach";                 ValueType: string;  ValueName: "";                  ValueData: "Attach";                                                    Tasks: context_vhd_attach;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Attach";                 ValueType: string;  ValueName: "Icon";              ValueData: """{app}\{#AppExe}""";                                      Tasks: context_vhd_attach;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Attach";                 ValueType: string;  ValueName: "MultiSelectModel";  ValueData: "Document";                                                  Tasks: context_vhd_attach;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Attach\command";         ValueType: string;  ValueName: "";                  ValueData: """{app}\{#AppExe}"" /attach ""%1""";                        Tasks: context_vhd_attach;

Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-AttachReadOnly";         ValueType: none;    Flags: deletekey uninsdeletekey;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-AttachReadOnly";         ValueType: string;  ValueName: "";                  ValueData: "Attach (read-only)";                                        Tasks: context_vhd_attachreadonly;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-AttachReadOnly";         ValueType: string;  ValueName: "Icon";              ValueData: """{app}\{#AppExe}""";                                      Tasks: context_vhd_attachreadonly;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-AttachReadOnly";         ValueType: string;  ValueName: "MultiSelectModel";  ValueData: "Document";                                                  Tasks: context_vhd_attachreadonly;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-AttachReadOnly\command"; ValueType: string;  ValueName: "";                  ValueData: """{app}\{#AppExe}"" /readonly /attach ""%1""";              Tasks: context_vhd_attachreadonly;

Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Detach";                 ValueType: none;    Flags: deletekey uninsdeletekey;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Detach";                 ValueType: string;  ValueName: "";                  ValueData: "Detach";                                                    Tasks: context_vhd_detach;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Detach";                 ValueType: string;  ValueName: "Icon";              ValueData: """{app}\{#AppExe}""";                                      Tasks: context_vhd_detach;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Detach";                 ValueType: string;  ValueName: "MultiSelectModel";  ValueData: "Document";                                                  Tasks: context_vhd_detach;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdAttach-Detach\command";         ValueType: string;  ValueName: "";                  ValueData: """{app}\{#AppExe}"" /detach ""%1""";                        Tasks: context_vhd_detach;

Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdStudio-Maintain";               ValueType: none;    Flags: deletekey uninsdeletekey;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdStudio-Maintain";               ValueType: string;  ValueName: "";                  ValueData: "Maintenance...";                                            Tasks: context_vhd_maintain;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdStudio-Maintain";               ValueType: string;  ValueName: "Icon";              ValueData: """{app}\{#AppExe}""";                                      Tasks: context_vhd_maintain;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdStudio-Maintain";               ValueType: string;  ValueName: "MultiSelectModel";  ValueData: "Single";                                                    Tasks: context_vhd_maintain;
Root: HKCR;  Subkey: "Windows.VhdFile\shell\VhdStudio-Maintain\command";       ValueType: string;  ValueName: "";                  ValueData: """{app}\{#AppExe}"" /maintain ""%1""";                      Tasks: context_vhd_maintain;

; Leftovers of VHD Attach 1.x-3.x
Root: HKCR;  Subkey: "VhdAttachFile";                                          ValueType: none;    Flags: deletekey;
Root: HKCR;  Subkey: "Drive\shell\VhdAttach-DetachDrive";                      ValueType: none;    Flags: deletekey uninsdeletekey;

Root: HKCR;  Subkey: ".iso";                                                   ValueType: none;    ValueName: "";                  Flags: deletevalue;                                                     Tasks: context_iso_open context_iso_attachreadonly context_iso_detach;
Root: HKCR;  Subkey: ".iso\OpenWithProgids";                                   ValueType: string;  ValueName: "Windows.IsoFile";   ValueData: "";                                                          Tasks: context_iso_open context_iso_attachreadonly context_iso_detach;
Root: HKCR;  Subkey: "Windows.IsoFile\shell";                                  ValueType: string;  ValueName: "";                  ValueData: "VhdAttach-Open";        Flags: uninsdeletevalue;            Tasks: context_iso_open;

Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-Open";                   ValueType: none;    Flags: deletekey uninsdeletekey;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-Open";                   ValueType: string;  ValueName: "";                  ValueData: "Open with {#AppName}";                                      Tasks: context_iso_open;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-Open";                   ValueType: string;  ValueName: "Icon";              ValueData: """{app}\{#AppExe}""";                                      Tasks: context_iso_open;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-Open";                   ValueType: string;  ValueName: "MultiSelectModel";  ValueData: "Document";                                                  Tasks: context_iso_open;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-Open\command";           ValueType: string;  ValueName: "";                  ValueData: """{app}\{#AppExe}"" ""%1""";                                Tasks: context_iso_open;

Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-AttachReadOnly";         ValueType: none;    Flags: deletekey uninsdeletekey;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-AttachReadOnly";         ValueType: string;  ValueName: "";                  ValueData: "Attach";                                                    Tasks: context_iso_attachreadonly;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-AttachReadOnly";         ValueType: string;  ValueName: "Icon";              ValueData: """{app}\{#AppExe}""";                                      Tasks: context_iso_attachreadonly;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-AttachReadOnly";         ValueType: string;  ValueName: "MultiSelectModel";  ValueData: "Document";                                                  Tasks: context_iso_attachreadonly;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-AttachReadOnly\command"; ValueType: string;  ValueName: "";                  ValueData: """{app}\{#AppExe}"" /readonly /attach ""%1""";              Tasks: context_iso_attachreadonly;

Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-Detach";                 ValueType: none;    Flags: deletekey uninsdeletekey;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-Detach";                 ValueType: string;  ValueName: "";                  ValueData: "Detach";                                                    Tasks: context_iso_detach;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-Detach";                 ValueType: string;  ValueName: "Icon";              ValueData: """{app}\{#AppExe}""";                                      Tasks: context_iso_detach;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-Detach";                 ValueType: string;  ValueName: "MultiSelectModel";  ValueData: "Document";                                                  Tasks: context_iso_detach;
Root: HKCR;  Subkey: "Windows.IsoFile\shell\VhdAttach-Detach\command";         ValueType: string;  ValueName: "";                  ValueData: """{app}\{#AppExe}"" /detach ""%1""";                        Tasks: context_iso_detach;


[Run]
Filename: "{app}\{#AppExe}";                                Flags: postinstall nowait skipifsilent runasoriginaluser unchecked;  Description: "Launch {#AppName} now";


[UninstallRun]
; Supersede these IDs in old, appended uninstall logs. Service cleanup now uses the native code below.
Filename: "{sys}\cmd.exe"; Parameters: "/D /C exit 0"; Flags: runhidden waituntilterminated; RunOnceId: "UninstallService"
Filename: "{sys}\cmd.exe"; Parameters: "/D /C exit 0"; Flags: runhidden waituntilterminated; RunOnceId: "EndServiceProcess"


[Code]

type
  TServiceStatus = record
    ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
    ServiceSpecificExitCode, CheckPoint, WaitHint: LongWord;
  end;
  TServiceStatusProcess = record
    ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
    ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags: LongWord;
  end;
  TProcessIds = array[0..4095] of LongWord;

function OpenSCManager(MachineName, DatabaseName: Integer; DesiredAccess: LongWord): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(Manager: THandle; Name: String; DesiredAccess: LongWord): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function CloseServiceHandle(Handle: THandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';
function QueryServiceStatusEx(Service: THandle; InfoLevel: LongWord;
  var Status: TServiceStatusProcess; BufferSize: LongWord; var BytesNeeded: LongWord): Boolean;
  external 'QueryServiceStatusEx@advapi32.dll stdcall';
function ControlService(Service: THandle; Control: LongWord; var Status: TServiceStatus): Boolean;
  external 'ControlService@advapi32.dll stdcall';
function DeleteService(Service: THandle): Boolean;
  external 'DeleteService@advapi32.dll stdcall';
function CreateService(Manager: THandle; Name, DisplayName: String;
  DesiredAccess, ServiceType, StartType, ErrorControl: LongWord; BinaryPath: String;
  LoadOrderGroup, TagId, Dependencies, ServiceStartName, Password: Integer): THandle;
  external 'CreateServiceW@advapi32.dll stdcall';
function ChangeServiceConfig(Service: THandle; ServiceType, StartType, ErrorControl: LongWord;
  BinaryPath: String; LoadOrderGroup, TagId, Dependencies, ServiceStartName, Password: Integer;
  DisplayName: String): Boolean;
  external 'ChangeServiceConfigW@advapi32.dll stdcall';
function StartService(Service: THandle; ArgCount, Args: LongWord): Boolean;
  external 'StartServiceW@advapi32.dll stdcall';
function OpenProcess(DesiredAccess: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function QueryFullProcessImageName(Process: THandle; Flags: LongWord; FileName: String;
  var Size: LongWord): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function TerminateProcess(Process: THandle; ExitCode: LongWord): Boolean;
  external 'TerminateProcess@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function EnumProcesses(var ProcessIds: TProcessIds; Size: LongWord; var BytesReturned: LongWord): Boolean;
  external 'EnumProcesses@psapi.dll stdcall';

const
  LegacyUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\JosipMedved_VhdAttach_is1';
  DesktopRuntimeKey  = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  RuntimeDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

procedure InitializeWizard;
begin
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;

function HasVersion10(const Names: TArrayOfString): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Names) - 1 do begin
    if (Pos('10.', Names[I]) = 1) and (Pos('-', Names[I]) = 0) then begin
      Result := True;
      Exit;
    end;
  end;
end;

{ Framework-dependent build: needs a stable .NET Desktop Runtime 10 (x64).
  The .NET installer registers versions in the 32-bit registry view (WOW6432Node); the runtime folder is checked as well. }
function IsDesktopRuntimeInstalled: Boolean;
var
  Names: TArrayOfString;
  FindRec: TFindRec;
begin
  Result := (RegGetValueNames(HKLM32, DesktopRuntimeKey, Names) and HasVersion10(Names))
         or (RegGetValueNames(HKLM64, DesktopRuntimeKey, Names) and HasVersion10(Names));
  if Result then Exit;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\*'), FindRec) then begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('10.', FindRec.Name) = 1) and (Pos('-', FindRec.Name) = 0) then begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
#ifdef SelfContained
  Exit; { runtime is embedded }
#endif
  if not IsDesktopRuntimeInstalled then begin
    if SuppressibleMsgBox('{#AppName} requires the .NET 10 Desktop Runtime (x64).' + #13#10#13#10 + 'Open the download page now? Run this setup again after installing it.', mbConfirmation, MB_YESNO, IDYES) = IDYES then
      ShellExec('open', RuntimeDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;

function ServiceError(const Operation: String): String;
var
  ErrorCode: LongWord;
begin
  ErrorCode := DLLGetLastError;
  Result := Operation + ': ' + SysErrorMessage(ErrorCode) + ' (' + IntToStr(ErrorCode) + ').';
  Log(Result);
end;

function ReadServiceStatus(Service: THandle; var Status: TServiceStatusProcess): String;
var
  BytesNeeded: LongWord;
begin
  Result := '';
  if not QueryServiceStatusEx(Service, 0, Status, SizeOf(Status), BytesNeeded) then
    Result := ServiceError('Cannot query the VHD Studio service');
end;

function EndServiceProcess(Process: THandle; GraceMilliseconds: LongWord): String;
var
  WaitResult: LongWord;
begin
  Result := '';
  WaitResult := WaitForSingleObject(Process, GraceMilliseconds);
  if WaitResult = 0 then Exit;
  if WaitResult <> $102 then begin Result := ServiceError('Cannot wait for the old VHD Studio service'); Exit; end;
  Log('Service did not exit; terminating its verified process.');
  if not TerminateProcess(Process, 0) then begin
    if WaitForSingleObject(Process, 0) = 0 then Exit; { exited between the wait and termination }
    Result := ServiceError('Cannot terminate the old VHD Studio service'); Exit;
  end;
  if WaitForSingleObject(Process, 5000) <> 0 then
    Result := 'The old VHD Studio service process did not exit. Restart Windows, then run setup again.';
end;

{ A previous setup may have left a foreground thread, pending service stop, or hidden
  /Uninstall host dialog alive. Match the complete installed path, never just its name. }
function EndInstalledServiceProcesses: String;
var
  ProcessIds: TProcessIds;
  BytesReturned, ImagePathLength: LongWord;
  Process: THandle;
  ImagePath: String;
  I: Integer;
begin
  Result := '';
  if not EnumProcesses(ProcessIds, SizeOf(ProcessIds), BytesReturned) then begin
    Result := ServiceError('Cannot check for old VHD Studio service processes'); Exit;
  end;
  if BytesReturned = SizeOf(ProcessIds) then begin
    Result := 'Too many processes to verify the old service safely. Restart Windows, then run setup again.'; Exit;
  end;
  for I := 0 to (BytesReturned div 4) - 1 do begin
    Process := OpenProcess($00101001, False, ProcessIds[I]);
    if Process <> 0 then begin
      try
        ImagePathLength := 32768;
        SetLength(ImagePath, ImagePathLength);
        if QueryFullProcessImageName(Process, 0, ImagePath, ImagePathLength) then begin
          SetLength(ImagePath, ImagePathLength);
          if CompareText(ImagePath, ExpandConstant('{app}\{#ServiceExe}')) = 0 then begin
            Log('Ending process from old installation, PID ' + IntToStr(ProcessIds[I]) + ': ' + ImagePath);
            Result := EndServiceProcess(Process, 0);
            if Result <> '' then Exit;
          end;
        end;
      finally
        CloseHandle(Process);
      end;
    end;
  end;
end;

{ Keep a handle to the SCM's process before requesting stop: older versions can report
  STOPPED while their process still holds files open. Never kill by image name or a reused PID. }
function StopServiceProcess(RemoveRegistration: Boolean): String;
var
  Manager, Service, Process: THandle;
  Status: TServiceStatusProcess;
  StopStatus: TServiceStatus;
  ImagePath: String;
  ImagePathLength, ErrorCode: LongWord;
  I: Integer;
begin
  Result := '';
  Process := 0;
  Manager := OpenSCManager(0, 0, $0001); { SC_MANAGER_CONNECT }
  if Manager = 0 then begin Result := ServiceError('Cannot open the service manager'); Exit; end;
  try
    Service := OpenService(Manager, 'VhdStudio', $00010024); { DELETE | QUERY_STATUS | STOP }
    if Service = 0 then begin
      if DLLGetLastError = 1060 then Result := EndInstalledServiceProcesses
      else Result := ServiceError('Cannot open the VHD Studio service');
      Exit;
    end;
    try
      Result := ReadServiceStatus(Service, Status);
      if Result <> '' then Exit;
      if Status.ServiceType <> $10 then begin
        Result := 'The VHD Studio service has an unexpected service type. Stop it manually before continuing.'; Exit;
      end;
      if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\VhdStudio', 'ImagePath', ImagePath) then begin
        Result := 'Cannot verify the registered VHD Studio service executable.'; Exit;
      end;
      if CompareText(RemoveQuotes(ImagePath), ExpandConstant('{app}\{#ServiceExe}')) <> 0 then begin
        Result := 'The VHD Studio service points to another installation: ' + ImagePath + '. Stop that service before continuing.'; Exit;
      end;
      if (Status.CurrentState >= 4) and (Status.CurrentState <= 7) and (Status.ProcessId <> 0) and
         (Status.ServiceType = $10) and (Status.ServiceFlags = 0) then begin
        Process := OpenProcess($00101001, False, Status.ProcessId); { SYNCHRONIZE | QUERY_LIMITED_INFORMATION | TERMINATE }
        if Process = 0 then begin Result := ServiceError('Cannot inspect the VHD Studio service process'); Exit; end;
        ImagePathLength := 32768;
        SetLength(ImagePath, ImagePathLength);
        if not QueryFullProcessImageName(Process, 0, ImagePath, ImagePathLength) then begin
          Result := ServiceError('Cannot verify the VHD Studio service executable'); Exit;
        end;
        SetLength(ImagePath, ImagePathLength);
        if CompareText(ImagePath, ExpandConstant('{app}\{#ServiceExe}')) <> 0 then begin
          Result := 'The VHD Studio service points to another installation: ' + ImagePath + '. Stop that service before continuing.';
          Exit;
        end;
        Log('Stopping VHD Studio service PID ' + IntToStr(Status.ProcessId) + ': ' + ImagePath);
      end;

      if (Status.CurrentState <> 1) and (Status.CurrentState <> 3) then begin
        if not ControlService(Service, 1, StopStatus) then begin { SERVICE_CONTROL_STOP }
          ErrorCode := DLLGetLastError;
          if (ErrorCode <> 1053) and (ErrorCode <> 1061) and (ErrorCode <> 1062) then begin
            Result := 'Cannot stop the VHD Studio service: ' + SysErrorMessage(ErrorCode) + '.'; Exit;
          end;
        end;
      end;
      for I := 1 to 80 do begin { at most 20 seconds for a graceful stop }
        Result := ReadServiceStatus(Service, Status);
        if (Result <> '') or (Status.CurrentState = 1) then Break;
        Sleep(250);
      end;
      if Result <> '' then Exit;

      if Process <> 0 then begin
        Result := EndServiceProcess(Process, 5000);
        if Result <> '' then Exit;
      end;
      { Pending-state SCM PIDs are undefined. After the grace period, use full-path
        process evidence instead; this also clears an already-stopped orphan. }
      Result := EndInstalledServiceProcesses;
      if Result <> '' then Exit;
      for I := 1 to 20 do begin { let SCM observe the process exit }
        Result := ReadServiceStatus(Service, Status);
        if (Result <> '') or (Status.CurrentState = 1) then Break;
        Sleep(250);
      end;
      if Result <> '' then Exit;
      if Status.CurrentState <> 1 then begin
        Result := 'The VHD Studio service did not stop. Restart Windows, then run setup again.'; Exit;
      end;
      if RemoveRegistration and not DeleteService(Service) then
        Result := ServiceError('Cannot remove the VHD Studio service');
    finally
      if Process <> 0 then CloseHandle(Process);
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(Manager);
  end;
end;

{ Install with native Windows APIs, so a missing runtime can never leave setup waiting
  on an invisible .NET host dialog in either the old or the newly installed executable. }
procedure InstallService;
var
  Manager, Service: THandle;
  Status: TServiceStatusProcess;
  Error, BinaryPath: String;
  I: Integer;
begin
  BinaryPath := '"' + ExpandConstant('{app}\{#ServiceExe}') + '"';
  Manager := OpenSCManager(0, 0, $0003); { CONNECT | CREATE_SERVICE }
  if Manager = 0 then RaiseException(ServiceError('Cannot open the service manager'));
  try
    Service := OpenService(Manager, 'VhdStudio', $0016); { CHANGE_CONFIG | QUERY_STATUS | START }
    if Service = 0 then begin
      if DLLGetLastError <> 1060 then RaiseException(ServiceError('Cannot open the VHD Studio service'));
      Service := CreateService(Manager, 'VhdStudio', '{#AppName}', $0016, $10, 2, 1,
        BinaryPath, 0, 0, 0, 0, 0); { own process, automatic, LocalSystem }
      if Service = 0 then RaiseException(ServiceError('Cannot create the VHD Studio service'));
    end;
    try
      if not ChangeServiceConfig(Service, $10, 2, 1, BinaryPath, 0, 0, 0, 0, 0, '{#AppName}') then
        RaiseException(ServiceError('Cannot configure the VHD Studio service'));
      if not StartService(Service, 0, 0) then
        RaiseException(ServiceError('Cannot start the VHD Studio service'));
      for I := 1 to 80 do begin
        Error := ReadServiceStatus(Service, Status);
        if Error <> '' then RaiseException(Error);
        if Status.CurrentState = 4 then Exit; { SERVICE_RUNNING }
        if Status.CurrentState = 1 then
          RaiseException('The VHD Studio service exited during startup: ' + SysErrorMessage(Status.Win32ExitCode) + '.');
        Sleep(250);
      end;
      RaiseException('The VHD Studio service did not start within 20 seconds. Check the Windows event log.');
    finally
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(Manager);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then InstallService;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Handler, Error: String;
begin
  if CurUninstallStep = usUninstall then begin
    Error := StopServiceProcess(True);
    if Error <> '' then begin
      SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
      Abort; { stop before removing files if the service is still using them }
    end;
    if RegQueryStringValue(HKCR, '.vsux', '', Handler) and (Handler = 'VhdStudio.Vsux') then
      RegDeleteValue(HKCR, '.vsux', '');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Uninstaller: String;
  AutoAttach: String;
begin
  { Do not execute the previous service binary: its runtime may be missing or its helper hung. }
  Result := StopServiceProcess(False);
  if Result <> '' then Exit;

  { Carry over the VHD Attach 4.x auto-mount list; its uninstaller deletes the old key. }
  if not RegValueExists(HKLM64, 'Software\xGND Software\VHD Studio', 'AutoAttachVhdList') then begin
    if RegQueryMultiStringValue(HKLM64, 'Software\Josip Medved\VHD Attach', 'AutoAttachVhdList', AutoAttach) or RegQueryMultiStringValue(HKLM32, 'Software\Josip Medved\VHD Attach', 'AutoAttachVhdList', AutoAttach) then
      RegWriteMultiStringValue(HKLM64, 'Software\xGND Software\VHD Studio', 'AutoAttachVhdList', AutoAttach);
  end;

  { Remove VHD Attach 4.x (same features, older identity). }
  if RegQueryStringValue(HKLM32, LegacyUninstallKey, 'UninstallString', Uninstaller) or RegQueryStringValue(HKLM64, LegacyUninstallKey, 'UninstallString', Uninstaller) then begin
    Exec(RemoveQuotes(Uninstaller), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
  Result := '';
end;
