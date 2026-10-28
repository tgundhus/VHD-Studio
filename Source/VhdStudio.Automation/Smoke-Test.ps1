param(
    [string] $Executable = (Join-Path $PSScriptRoot 'bin/Debug/net10.0-windows/VhdStudio.Automation.exe')
)

# Exercises the real executable and SDK transport without attaching or formatting any disk.
$ErrorActionPreference = 'Stop'
$testExecutable = (Resolve-Path -LiteralPath $Executable).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('VhdStudioAutomationSmoke-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null

function Start-Automation([string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($testExecutable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    return $process
}

function Invoke-Cli([string[]] $Arguments, [string] $InputJson = '{}') {
    $process = Start-Automation $Arguments
    try {
        $process.StandardInput.Write($InputJson)
        $process.StandardInput.Close()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(15000)) { throw 'CLI did not exit within 15 seconds.' }
        return @{ ExitCode = $process.ExitCode; Json = ($stdout.GetAwaiter().GetResult() | ConvertFrom-Json); Error = $stderr.GetAwaiter().GetResult() }
    } finally {
        if (!$process.HasExited) { $process.Kill($true) }
        $process.Dispose()
    }
}

function Read-Response($Process, [int] $Id) {
    while ($true) {
        $line = $Process.StandardOutput.ReadLineAsync()
        if (!$line.Wait(15000)) { throw "MCP response $Id timed out." }
        if ($null -eq $line.Result) { throw "MCP exited before response $Id." }
        $response = $line.Result | ConvertFrom-Json
        if ($response.id -eq $Id) { return $response }
    }
}

function Send-Request($Process, [int] $Id, [string] $Method, $Parameters) {
    $request = @{ jsonrpc = '2.0'; id = $Id; method = $Method; params = $Parameters }
    $Process.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 20 -Compress))
    $Process.StandardInput.Flush()
    return Read-Response $Process $Id
}

try {
    $capabilities = Invoke-Cli @('capabilities')
    if ($capabilities.ExitCode -ne 0 -or !$capabilities.Json.success -or $capabilities.Json.data.operations.Count -ne 6) { throw 'CLI capabilities failed.' }
    $badInput = Invoke-Cli @('--root', $scratch, 'image_list') '['
    if ($badInput.ExitCode -ne 2 -or $badInput.Json.success -or $badInput.Json.error.code -ne 'invalid_argument') { throw 'Malformed JSON was not rejected as structured CLI error.' }
    $tooLarge = Invoke-Cli @('--root', $scratch, 'image_list') (' ' * 65537)
    if ($tooLarge.ExitCode -ne 2 -or $tooLarge.Json.success -or $tooLarge.Json.error.code -ne 'invalid_argument') { throw 'Oversized JSON input was not rejected.' }
    $list = Invoke-Cli @('--root', $scratch, 'image_list')
    if ($list.ExitCode -ne 0 -or !$list.Json.success) { throw 'Read-only CLI image listing failed.' }
    $create = Invoke-Cli @('--root', $scratch, 'image_create') '{"path":"refused.vhdx","sizeBytes":67108864}'
    if ($create.ExitCode -ne 1 -or $create.Json.success -or (Test-Path -LiteralPath (Join-Path $scratch 'refused.vhdx'))) { throw 'CLI mutation was not refused without --allow-write.' }

    $server = Start-Automation @('--root', $scratch, '--mcp')
    try {
        $errors = $server.StandardError.ReadToEndAsync()
        $hello = Send-Request $server 1 'initialize' @{ protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'vhdstudio-smoke'; version = '1' } }
        if (!$hello.result.capabilities.tools) { throw 'MCP did not advertise tools for a legacy client.' }
        $server.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $server.StandardInput.Flush()
        $tools = Send-Request $server 2 'tools/list' @{}
        if ($tools.result.tools.Count -ne 6) { throw 'MCP tool catalog is incomplete.' }
        $attached = $tools.result.tools | Where-Object name -eq 'image_attach'
        if ($attached.inputSchema.properties.readOnly.default -ne $true) { throw 'MCP attach schema does not default to read-only.' }
        $listed = Send-Request $server 3 'tools/call' @{ name = 'image_list'; arguments = @{} }
        if ($listed.result.isError -or !$listed.result.structuredContent.success) { throw 'MCP image_list failed.' }
        $denied = Send-Request $server 4 'tools/call' @{ name = 'image_create'; arguments = @{ path = 'refused.vhdx'; sizeBytes = 67108864 } }
        if (!$denied.result.isError -or $denied.result.structuredContent.success -or (Test-Path -LiteralPath (Join-Path $scratch 'refused.vhdx'))) { throw 'MCP mutation was not refused without --allow-write.' }
        $server.StandardInput.Close()
        if (!$server.WaitForExit(15000)) { throw 'MCP did not exit after stdin closed.' }
        if ($server.ExitCode -ne 0) { throw ('MCP exited with an error: ' + $errors.GetAwaiter().GetResult()) }
    } finally {
        if (!$server.HasExited) { $server.Kill($true) }
        $server.Dispose()
    }
    $modern = Start-Automation @('--root', $scratch, '--mcp')
    try {
        $modernErrors = $modern.StandardError.ReadToEndAsync()
        $metadata = @{
            'io.modelcontextprotocol/protocolVersion' = '2026-07-28'
            'io.modelcontextprotocol/clientInfo' = @{ name = 'vhdstudio-smoke'; version = '1' }
            'io.modelcontextprotocol/clientCapabilities' = @{}
        }
        $discovery = Send-Request $modern 1 'server/discover' @{ _meta = $metadata }
        if ($discovery.result.supportedVersions -notcontains '2026-07-28') { throw 'Modern MCP discovery failed.' }
        $modernTools = Send-Request $modern 2 'tools/list' @{ _meta = $metadata }
        if ($modernTools.result.resultType -ne 'complete' -or $modernTools.result.tools.Count -ne 6) { throw 'Modern MCP tool listing failed.' }
        $modernList = Send-Request $modern 3 'tools/call' @{ _meta = $metadata; name = 'image_list'; arguments = @{} }
        if ($modernList.result.isError -or !$modernList.result.structuredContent.success) { throw 'Modern MCP image_list failed.' }
        $modern.StandardInput.Close()
        if (!$modern.WaitForExit(15000)) { throw 'Modern MCP did not exit after stdin closed.' }
        if ($modern.ExitCode -ne 0) { throw ('Modern MCP exited with an error: ' + $modernErrors.GetAwaiter().GetResult()) }
    } finally {
        if (!$modern.HasExited) { $modern.Kill($true) }
        $modern.Dispose()
    }
    Write-Host 'Automation CLI and legacy/modern MCP smoke checks passed; no disks were attached or formatted.'
} finally {
    # Delete only this run's unique scratch directory, after checking its absolute location.
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (!$resolvedScratch.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or !([IO.Path]::GetFileName($resolvedScratch).StartsWith('VhdStudioAutomationSmoke-', [StringComparison]::Ordinal))) { throw 'Unsafe scratch cleanup path.' }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
