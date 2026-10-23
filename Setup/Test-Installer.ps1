<#
.SYNOPSIS
    Exercises real installers and upgrades only on disposable GitHub-hosted Windows runners.
.DESCRIPTION
    Installs the previous release, upgrades services that hang while stopping or remain alive
    after reporting stopped, switches between runtime modes, then uninstalls. Never run on
    a development PC: the script deliberately replaces its own temporary VhdStudio service.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PreviousInstaller,
    [Parameter(Mandatory)][string] $RegularInstaller,
    [Parameter(Mandatory)][string] $StandaloneInstaller,
    [string] $ResultsDirectory = (Join-Path $PSScriptRoot '..\TestResults\Installer')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or -not $IsWindows) {
    throw 'Installer smoke tests are restricted to disposable GitHub-hosted Windows runners.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The disposable runner must be elevated.'
}
if (Get-Service VhdStudio, VhdAttach -ErrorAction SilentlyContinue) { throw 'A pre-existing product service is present; refusing to change it.' }
if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VhdStudio_is1') {
    throw 'A pre-existing VHD Studio installation is present; refusing to change it.'
}
$savedSettings = Get-ItemProperty 'HKLM:\SOFTWARE\xGND Software\VHD Studio' -ErrorAction SilentlyContinue
if ($savedSettings -and $savedSettings.PSObject.Properties['AutoAttachVhdList']) {
    throw 'A saved auto-mount list is present; refusing to start the product service.'
}
foreach ($installer in $PreviousInstaller, $RegularInstaller, $StandaloneInstaller) {
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer not found: $installer" }
}

$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null
$testRoot = Join-Path $env:RUNNER_TEMP ('VhdStudio-Installer-' + [guid]::NewGuid().ToString('N'))
$appDirectory = Join-Path $testRoot 'Installed App'
if ($appDirectory.Length -ge 128) { throw 'The fixture path must be shorter than 128 characters.' }
$servicePath = Join-Path $appDirectory 'VhdStudioService.exe'
New-Item -ItemType Directory -Path $testRoot, (Join-Path $testRoot 'Fixture'), (Join-Path $testRoot 'Unrelated') | Out-Null
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()

function Invoke-Bounded([string] $Executable, [string[]] $Arguments, [int] $ExpectedExit = 0, [int] $TimeoutSeconds = 120) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($Executable)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    $ownedProcesses.Add($process)
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw "Timed out after ${TimeoutSeconds}s: $Executable $Arguments"
    }
    if ($process.ExitCode -ne $ExpectedExit) { throw "Unexpected exit $($process.ExitCode), expected ${ExpectedExit}: $Executable $Arguments" }
}

function Get-ServiceRecord {
    Get-CimInstance Win32_Service -Filter "Name='VhdStudio'"
}

function Wait-ServiceState([string] $State) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $record = Get-ServiceRecord
        if ($record -and $record.State -eq $State) { return $record }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "VhdStudio did not reach $State. Current state: $($record.State)"
}

function Get-DiskSnapshot {
    @(Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_Disk | ForEach-Object {
        '{0}|{1}|{2}|{3}' -f $_.ObjectId, $_.Number, $_.Location, $_.Size
    } | Sort-Object) -join "`n"
}

function Assert-Runtime([string] $CaseName, [bool] $Standalone, [bool] $RequireGlobalSearch) {
    $record = Wait-ServiceState 'Running'
    if ($record.PathName.Trim('"') -ne $servicePath) { throw "Service uses an unexpected executable: $($record.PathName)" }
    $process = Get-Process -Id $record.ProcessId
    $coreClr = @($process.Modules | Where-Object ModuleName -eq 'coreclr.dll')
    if ($coreClr.Count -ne 1) { throw 'The installed service did not load CoreCLR.' }
    $localRuntime = $coreClr[0].FileName.StartsWith($appDirectory + '\', [StringComparison]::OrdinalIgnoreCase)
    if ($localRuntime -ne $Standalone) { throw "Wrong service runtime for ${CaseName}: $($coreClr[0].FileName)" }

    # This existing command exits 1 from managed Main when no image is supplied. A hostfxr
    # bootstrap error has a different exit code, so this also catches the .NET download prompt.
    $trace = Join-Path $ResultsDirectory ($CaseName + '-host.txt')
    $startInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path $appDirectory 'VhdStudio.exe'))
    $startInfo.UseShellExecute = $false
    $startInfo.ArgumentList.Add('/attach')
    $startInfo.Environment['DOTNET_DISABLE_GUI_ERRORS'] = '1'
    $startInfo.Environment['DOTNET_HOST_TRACE'] = '1'
    $startInfo.Environment['DOTNET_HOST_TRACEFILE'] = $trace
    $startInfo.Environment['COREHOST_TRACE'] = '1'
    $startInfo.Environment['COREHOST_TRACEFILE'] = $trace
    $app = [Diagnostics.Process]::Start($startInfo)
    $ownedProcesses.Add($app)
    if (-not $app.WaitForExit(20000)) { $app.Kill($true); throw "Installed app startup timed out: $CaseName" }
    if ($app.ExitCode -ne 1) { throw "Installed app did not reach managed startup: $CaseName (exit $($app.ExitCode)); inspect $trace" }
    if (-not (Test-Path -LiteralPath $trace)) { throw "Host trace was not created: $CaseName" }
    $hostTrace = Get-Content -LiteralPath $trace -Raw
    if ($RequireGlobalSearch -and -not $Standalone -and $hostTrace -notmatch 'Using global install location') {
        throw "The framework-dependent app did not resolve the global runtime: $CaseName; inspect $trace"
    }
    Write-Host "PASS $CaseName (app bootstrap; service runtime $($coreClr[0].FileName))"
}

function Install-Product([string] $Installer, [string] $CaseName, [bool] $Standalone, [bool] $RequireGlobalSearch = $true) {
    Invoke-Bounded $Installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR=' + $appDirectory), ('/LOG=' + (Join-Path $ResultsDirectory ($CaseName + '-setup.log'))))
    Assert-Runtime $CaseName $Standalone $RequireGlobalSearch
}

function Start-HungFixture([string] $Mode) {
    Invoke-Bounded "$env:SystemRoot\System32\sc.exe" @('stop', 'VhdStudio')
    $null = Wait-ServiceState 'Stopped'
    foreach ($process in @(Get-Process VhdStudioService -ErrorAction SilentlyContinue)) {
        if ($process.Path -eq $servicePath -and -not $process.WaitForExit(10000)) { throw 'The real service failed to stop before fixture replacement.' }
    }
    Copy-Item -LiteralPath $fixtureExe -Destination $servicePath -Force
    Set-Content -LiteralPath ($servicePath + '.mode') -Value $Mode -Encoding ascii
    Invoke-Bounded "$env:SystemRoot\System32\sc.exe" @('config', 'VhdStudio', 'binPath=', ('"' + $servicePath + '"'))
    Invoke-Bounded "$env:SystemRoot\System32\sc.exe" @('start', 'VhdStudio')
    $record = Wait-ServiceState 'Running'
    $process = Get-Process -Id $record.ProcessId
    $null = $process.Handle # Keep the original process identity even if its PID is reused later.
    $ownedProcesses.Add($process)
    return $process
}

$fixtureSource = @'
using System;
using System.IO;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
sealed class HungService : ServiceBase {
    static bool lingering;
    HungService() { ServiceName = "VhdStudio"; CanStop = true; AutoLog = false; }
    static void Main(string[] args) {
        if (args.Length > 0 && (args[0] == "/Sleep" || args[0] == "/Uninstall")) {
            Thread.Sleep(Timeout.Infinite); return;
        }
        lingering = File.ReadAllText(Assembly.GetExecutingAssembly().Location + ".mode").Trim() == "Lingering";
        ServiceBase.Run(new HungService());
        if (lingering) Thread.Sleep(Timeout.Infinite);
    }
    protected override void OnStart(string[] args) { }
    protected override void OnStop() {
        if (!lingering) { RequestAdditionalTime(120000); Thread.Sleep(Timeout.Infinite); }
    }
}
'@
$fixtureFile = Join-Path $testRoot 'Fixture\HungService.cs'
$fixtureExe = Join-Path $testRoot 'Fixture\VhdStudioService.exe'
Set-Content -LiteralPath $fixtureFile -Value $fixtureSource
$framework = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe ('/reference:' + (Join-Path $framework 'System.ServiceProcess.dll')) ('/out:' + $fixtureExe) $fixtureFile
if ($LASTEXITCODE -ne 0) { throw 'Cannot compile the dependency-free service fixture.' }
$disksBefore = Get-DiskSnapshot
$unrelated = $null
Start-Transcript -Path (Join-Path $ResultsDirectory 'smoke.log') -Force | Out-Null
try {
    Install-Product $PreviousInstaller 'baseline-previous-release' $false $false
    $unrelatedPath = Join-Path $testRoot 'Unrelated\VhdStudioService.exe'
    Copy-Item -LiteralPath $fixtureExe -Destination $unrelatedPath
    $unrelated = Start-Process -FilePath $unrelatedPath -ArgumentList '/Sleep' -PassThru -WindowStyle Hidden
    $ownedProcesses.Add($unrelated)

    $hung = Start-HungFixture 'Pending'
    Install-Product $RegularInstaller 'upgrade-hung-stop' $false
    if (-not $hung.HasExited) { throw 'The hung service process survived the upgrade.' }
    if ($unrelated.HasExited) { throw 'The installer killed an unrelated process with the same filename.' }

    Install-Product $StandaloneInstaller 'regular-to-standalone' $true
    if (-not (Test-Path -LiteralPath (Join-Path $appDirectory 'hostfxr.dll'))) { throw 'Standalone runtime fixture is missing hostfxr.dll.' }
    Install-Product $RegularInstaller 'standalone-to-regular' $false

    $lingering = Start-HungFixture 'Lingering'
    Install-Product $StandaloneInstaller 'upgrade-stopped-process-alive' $true
    if (-not $lingering.HasExited) { throw 'The stopped service process survived the upgrade.' }
    if ($unrelated.HasExited) { throw 'An unrelated same-name process was killed during upgrade.' }

    $orphan = Start-HungFixture 'Lingering'
    Invoke-Bounded "$env:SystemRoot\System32\sc.exe" @('stop', 'VhdStudio')
    $null = Wait-ServiceState 'Stopped'
    if ($orphan.HasExited) { throw 'The initial-stopped fixture did not remain alive.' }
    Install-Product $RegularInstaller 'upgrade-already-stopped-process-alive' $false
    if (-not $orphan.HasExited) { throw 'The already-stopped service process survived the upgrade.' }
    if ($unrelated.HasExited) { throw 'An unrelated same-name process was killed during orphan cleanup.' }

    $installedService = Get-Process -Id (Get-ServiceRecord).ProcessId
    $null = $installedService.Handle
    $ownedProcesses.Add($installedService)
    $uninstallLog = Join-Path $ResultsDirectory 'uninstall.log'
    Invoke-Bounded (Join-Path $appDirectory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG=' + $uninstallLog))
    if (-not $installedService.HasExited) { throw 'Uninstall left its service process running.' }
    if (Test-Path -LiteralPath $servicePath) { throw 'Uninstall left the installed service executable on disk.' }
    if ((Get-Content -LiteralPath $uninstallLog -Raw) -match 'Failed to delete the file') { throw 'Uninstall failed to delete installed files; inspect its log.' }
    if (Get-Service VhdStudio -ErrorAction SilentlyContinue) { throw 'Uninstall left its service registered.' }
    if ($unrelated.HasExited) { throw 'Uninstall killed an unrelated same-name process.' }
    if ((Get-DiskSnapshot) -ne $disksBefore) { throw 'Disk inventory changed while exercising installers.' }
    Write-Host 'PASS installer upgrades, both runtime modes, exact-process shutdown and uninstall'
} finally {
    # Cleanup is restricted to the test-owned path and recorded child process handles.
    $record = Get-ServiceRecord
    if ($record -and $record.PathName.TrimStart('"').StartsWith($appDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
        if ($record.ProcessId) {
            $process = Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue
            if ($process -and $process.Path.StartsWith($appDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) { $process.Kill($true); $null = $process.WaitForExit(5000) }
        }
        & "$env:SystemRoot\System32\sc.exe" delete VhdStudio | Out-Null
    }
    foreach ($process in $ownedProcesses) {
        if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(5000) }
        $process.Dispose()
    }
    Stop-Transcript | Out-Null
}
