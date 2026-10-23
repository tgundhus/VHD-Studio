param([switch]$Elevated)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$testOutput = Join-Path $projectRoot 'TestResults'
New-Item -ItemType Directory -Path $testOutput -Force | Out-Null
$logPath = Join-Path $testOutput 'native-checks.log'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    if ($Elevated) { throw 'The elevated helper did not receive an Administrator token.' }
    $hostExe = (Get-Process -Id $PID).Path
    $helper = Start-Process -FilePath $hostExe -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'), '-Elevated'
    )
    $helper.WaitForExit()
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath }
    exit $helper.ExitCode
}
Start-Transcript -LiteralPath $logPath -Force | Out-Null
$testExit = 1
try {
    $testExe = Join-Path $projectRoot 'bin\Debug\net10.0-windows\VsuPrototype.exe'
    if (-not (Test-Path -LiteralPath $testExe)) { throw 'Build VsuPrototype.csproj first.' }
    $repositoryRoot = Split-Path (Split-Path $projectRoot -Parent) -Parent
    $env:VSUX_RECOVERY_EXE = Join-Path $repositoryRoot 'Publish\VhdStudio\Recovery\VsuRecover.exe'
    if (-not (Test-Path -LiteralPath $env:VSUX_RECOVERY_EXE)) {
        $env:VSUX_RECOVERY_EXE = Join-Path $projectRoot 'Recovery\bin\Debug\net10.0-windows\VsuRecover.exe'
    }
    if (-not (Test-Path -LiteralPath $env:VSUX_RECOVERY_EXE)) { throw 'Build the standalone recovery project first.' }
    & $testExe --self-test-native
    $testExit = $LASTEXITCODE
} finally {
    Stop-Transcript | Out-Null
}
exit $testExit
