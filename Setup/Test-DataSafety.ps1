<#
.SYNOPSIS
    Runs the elevated data-safety test suite (real virtual disks, byte-for-byte verification).

.DESCRIPTION
    Creates scratch VHD/VHDX files under %TEMP%\VhdStudioSafetyTests, attaches them, writes random data,
    runs every maintenance and Disk Manager operation and verifies all data afterwards with SHA-256.
    Only disks backed by those scratch files are ever touched. Requires administrator rights;
    re-launches itself elevated when needed.

.PARAMETER LogFile
    Where to write the test output (default: TestResults\data-safety.log in the repository).
#>
[CmdletBinding()]
param(
    [string] $LogFile
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $LogFile) { $LogFile = Join-Path $root 'TestResults\data-safety.log' }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Requesting administrator rights...'
    $process = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-LogFile', "`"$LogFile`"")
    if (Test-Path $LogFile) { Get-Content $LogFile | Select-Object -Last 40 }
    exit $process.ExitCode
}

New-Item -ItemType Directory -Force (Split-Path $LogFile) | Out-Null
$scratch = Join-Path $env:TEMP 'VhdStudioSafetyTests'
try {
    dotnet test (Join-Path $root 'Source\VhdAttach-Test\VhdAttach-Test.csproj') -- --filter 'TestCategory=Elevated' 2>&1 | Tee-Object -FilePath $LogFile
    $code = $LASTEXITCODE
} finally {
    if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue } #only our scratch files live here
}
exit $code
