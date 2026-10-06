<#
.SYNOPSIS
    Builds VHD Studio, optionally signs it, and creates the Inno Setup installer.

.DESCRIPTION
    1. dotnet test (unless -SkipTests)
    2. dotnet publish both executables into ..\Publish (framework-dependent, win-x64)
    3. optional Authenticode signing (-CertificateThumbprint)
    4. ISCC.exe Setup\VhdStudio.iss -> ..\Releases\vhdstudio-<version>-setup.exe

.EXAMPLE
    .\Publish.ps1
.EXAMPLE
    .\Publish.ps1 -CertificateThumbprint 0123abcd... -TimestampUrl http://timestamp.digicert.com
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $SkipTests,
    [switch] $SkipInstaller,
    [switch] $SelfContained     #embed the .NET runtime (standalone installer, no prerequisite)
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'Source\VhdStudio.sln'
$publishDir = Join-Path $root 'Publish'
$releaseDir = Join-Path $root 'Releases'

function Invoke-Step([string] $Title, [scriptblock] $Action) {
    Write-Host "--- $Title" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$Title failed (exit code $LASTEXITCODE)." }
    Write-Host ''
}

$hash = (git -C $root log -n 1 --format=%h 2>$null)
if ($hash -and (git -C $root status --porcelain)) { $hash += '+' }

if (-not $SkipTests) {
    Invoke-Step 'Test' { dotnet test $solution -c $Configuration --nologo }
}

Invoke-Step 'Publish' {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    foreach ($project in 'VhdAttach\VhdAttach.csproj', 'VhdAttach-Service\VhdAttach-Service.csproj') {
        dotnet publish (Join-Path $root "Source\$project") -c $Configuration -r win-x64 --self-contained $(if ($SelfContained) { 'true' } else { 'false' }) -o $publishDir --nologo
        if ($LASTEXITCODE -ne 0) { return }
    }
}

if ($CertificateThumbprint) {
    Invoke-Step 'Sign executables' {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK).' }
        $files = Get-ChildItem $publishDir -Include 'VhdStudio*.exe', 'VhdStudio*.dll' -Recurse
        & $signtool.FullName sign /fd SHA256 /sha1 $CertificateThumbprint /tr $TimestampUrl /td SHA256 $files.FullName
    }
}

if (-not $SkipInstaller) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source }
    if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isinfo.php or run with -SkipInstaller.' }

    Invoke-Step 'Build installer' {
        New-Item -ItemType Directory -Force $releaseDir | Out-Null
        $defines = @("/DVersionHash=$hash")
        if ($SelfContained) { $defines += '/DSelfContained' }
        & $iscc @defines (Join-Path $PSScriptRoot 'VhdStudio.iss')
    }

    if ($CertificateThumbprint) {
        Invoke-Step 'Sign installer' {
            $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
            $setup = Get-ChildItem $releaseDir -Filter 'vhdstudio-*-setup*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            & $signtool.FullName sign /fd SHA256 /sha1 $CertificateThumbprint /tr $TimestampUrl /td SHA256 $setup.FullName
        }
    }
    Get-ChildItem $releaseDir -Filter 'vhdstudio-*-setup*.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | ForEach-Object { Write-Host "Created $($_.FullName)" -ForegroundColor Green }
} else {
    Write-Host "Binaries are in $publishDir" -ForegroundColor Green
}
