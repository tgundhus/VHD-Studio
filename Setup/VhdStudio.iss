; VHD Studio setup (Inno Setup 6). Build with Setup\Publish.ps1, which publishes the binaries into ..\Publish first.

#define AppExe         "VhdStudio.exe"
#define ServiceExe     "VhdStudioService.exe"
#define AppName        GetStringFileInfo('..\Publish\' + AppExe, 'ProductName')
#define AppVersion     GetStringFileInfo('..\Publish\' + AppExe, 'ProductVersion')
#define AppFileVersion GetStringFileInfo('..\Publish\' + AppExe, 'FileVersion')
#define AppCompany     GetStringFileInfo('..\Publish\' + AppExe, 'CompanyName')
#define AppCopyright   GetStringFileInfo('..\Publish\' + AppExe, 'LegalCopyright')
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
OutputDir=..\Releases
SourceDir=..\Publish
AppId=VhdStudio
CloseApplications=yes
RestartApplications=no
AppMutex=Global\VhdStudio
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Source\VhdAttach\Properties\App.ico
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
LicenseFile=..\Setup\License.rtf


[Messages]
SetupAppTitle=Setup {#AppName} {#AppVersionEx}
SetupWindowTitle=Setup {#AppName} {#AppVersionEx}
BeveledLabel={#AppName}


[Files]
Source: "*";                     DestDir: "{app}";  Excludes: "*.pdb";        Flags: ignoreversion recursesubdirs;
Source: "..\README.md";          DestDir: "{app}";  DestName: "ReadMe.txt";   Flags: overwritereadonly uninsremovereadonly;  Attribs: readonly;
Source: "..\LICENSE.md";         DestDir: "{app}";  DestName: "License.txt";  Flags: overwritereadonly uninsremovereadonly;  Attribs: readonly;


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
Filename: "{app}\{#ServiceExe}";  Parameters: "/Install";  StatusMsg: "Installing service...";  Flags: runascurrentuser waituntilterminated runhidden;
Filename: "{app}\{#AppExe}";                                Flags: postinstall nowait skipifsilent runasoriginaluser unchecked;  Description: "Launch {#AppName} now";


[UninstallRun]
Filename: "{app}\{#ServiceExe}";  Parameters: "/Uninstall";  Flags: runascurrentuser waituntilterminated runhidden;  RunOnceId: "UninstallService"
Filename: "{sys}\taskkill.exe";   Parameters: "/F /IM {#ServiceExe}";  Flags: runhidden waituntilterminated;  RunOnceId: "EndServiceProcess"


[Code]

const
  LegacyUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\JosipMedved_VhdAttach_is1';
  DesktopRuntimeKey  = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  RuntimeDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

procedure InitializeWizard;
begin
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;

function HasVersion10OrNewer(const Names: TArrayOfString): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Names) - 1 do begin
    if StrToIntDef(Copy(Names[I], 1, Pos('.', Names[I]) - 1), 0) >= 10 then begin
      Result := True;
      Exit;
    end;
  end;
end;

{ Framework-dependent build: needs the .NET Desktop Runtime 10 or newer (x64).
  The .NET installer registers versions in the 32-bit registry view (WOW6432Node); the runtime folder is checked as well. }
function IsDesktopRuntimeInstalled: Boolean;
var
  Names: TArrayOfString;
  FindRec: TFindRec;
begin
  Result := (RegGetValueNames(HKLM32, DesktopRuntimeKey, Names) and HasVersion10OrNewer(Names))
         or (RegGetValueNames(HKLM64, DesktopRuntimeKey, Names) and HasVersion10OrNewer(Names));
  if Result then Exit;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\*'), FindRec) then begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (StrToIntDef(Copy(FindRec.Name, 1, Pos('.', FindRec.Name) - 1), 0) >= 10) then begin
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

{ VHD Studio 5.0.0's service process could stay alive after "stop" and lock its files.
  Make sure it is gone before files are replaced: stop, give it time, then end the process as a last resort. }
procedure StopServiceProcess;
var
  ResultCode, I: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop VhdStudio', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for I := 1 to 10 do begin
    if not Exec(ExpandConstant('{sys}\cmd.exe'), '/c tasklist /FI "IMAGENAME eq {#ServiceExe}" | find /I "{#ServiceExe}" >nul', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then Exit;
    Sleep(500);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#ServiceExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Uninstaller: String;
  AutoAttach: String;
begin
  { Stop and remove the service of a previous VHD Studio version. }
  Exec(ExpandConstant('{app}\{#ServiceExe}'), '/Uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  StopServiceProcess;

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
