#!/usr/bin/env pwsh
# Builds the UE5 editor target and runs the headless smoke test.
[CmdletBinding()]
param(
    [string]$Engine = 'C:\Program Files\Epic Games\UE_5.8',
    [string]$Url = 'ws://127.0.0.1:8080/ws',
    [switch]$SkipBuild,
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'clients\ue5\UE5GameClient'
$project = Join-Path $projectDir 'UE5GameClient.uproject'
$ueLog = Join-Path $projectDir 'Saved\Logs\UE5GameClient.log'
$stdoutLog = Join-Path $root 'artifacts\logs\ue-smoke.stdout.log'
$buildBat = Join-Path $Engine 'Engine\Build\BatchFiles\Build.bat'
$editor = Join-Path $Engine 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'

if (-not (Test-Path $editor)) { throw "UnrealEditor-Cmd.exe not found under $Engine" }
New-Item -ItemType Directory -Force -Path (Split-Path $stdoutLog) | Out-Null

if (-not $SkipBuild) {
    Write-Host '==> Building UE5GameClientEditor' -ForegroundColor Cyan
    & $buildBat UE5GameClientEditor Win64 Development "-Project=$project" -WaitMutex -FromMsBuild
    if ($LASTEXITCODE -ne 0) { throw "UBT build failed with exit code $LASTEXITCODE" }
}

Remove-Item $ueLog -Force -ErrorAction SilentlyContinue
Remove-Item $stdoutLog -Force -ErrorAction SilentlyContinue

Write-Host "==> UE5 smoke test ($Url)" -ForegroundColor Cyan
$editorArgs = @(
    $project,
    '-game',
    '-nullrhi',
    '-nosplash',
    '-unattended',
    '-AI003Smoke',
    "-AI003Url=$Url",
    '-log'
)

$process = Start-Process -FilePath $editor -ArgumentList $editorArgs -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdoutLog -RedirectStandardError "$stdoutLog.err"

$finished = $process.WaitForExit($TimeoutSeconds * 1000)
$exitCode = $null
if ($finished) {
    try { $exitCode = $process.ExitCode } catch { $exitCode = $null }
}
else {
    try { $process.Kill() } catch { }
    throw "UE5 smoke test timed out after $TimeoutSeconds s; see $ueLog"
}

# UnrealEditor-Cmd writes the game log to Saved/Logs, not to stdout.
$marker = $null
if (Test-Path $ueLog) {
    $marker = Select-String -Path $ueLog -Pattern '\[AI003\]\[SMOKE\] (PASS|FAIL)' -ErrorAction SilentlyContinue |
        Select-Object -Last 1
}
if ($marker) { Write-Host $marker.Line }

$succeeded = $marker -and ($marker.Line -match 'PASS')
if (-not $succeeded -or ($exitCode -ne $null -and $exitCode -ne 0)) {
    if (Test-Path $ueLog) { Write-Host '--- last 15 log lines ---'; Get-Content $ueLog -Tail 15 }
    throw "UE5 smoke test failed (exit=$exitCode); see $ueLog"
}

Write-Host 'UE5 smoke test passed.' -ForegroundColor Green
