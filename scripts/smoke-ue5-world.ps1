#!/usr/bin/env pwsh
# Builds the UE5 editor target (unless -SkipBuild) and runs the headless world
# smoke test: terrain builds, the player lands on it and runs.
[CmdletBinding()]
param(
    [string]$Engine = 'C:\Program Files\Epic Games\UE_5.8',
    [int]$TimeoutSeconds = 240,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'clients\ue5\UE5GameClient'
$project = Join-Path $projectDir 'UE5GameClient.uproject'
$ueLog = Join-Path $projectDir 'Saved\Logs\UE5GameClient.log'
$stdoutLog = Join-Path $root 'artifacts\logs\ue-world-smoke.stdout.log'
$buildBat = Join-Path $Engine 'Engine\Build\BatchFiles\Build.bat'
$editor = Join-Path $Engine 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'

if (-not (Test-Path $editor)) { throw "UnrealEditor-Cmd.exe not found under $Engine" }
New-Item -ItemType Directory -Force -Path (Split-Path $stdoutLog) | Out-Null

if (-not $SkipBuild) {
    Write-Host '==> Building UE5GameClientEditor' -ForegroundColor Cyan
    & $buildBat UE5GameClientEditor Win64 Development "-Project=$project" -WaitMutex -FromMsBuild
    if ($LASTEXITCODE -ne 0) { throw "UBT build failed with exit code $LASTEXITCODE" }
}

Remove-Item $ueLog, $stdoutLog -Force -ErrorAction SilentlyContinue

Write-Host '==> UE5 world smoke test' -ForegroundColor Cyan
$process = Start-Process -FilePath $editor -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdoutLog -RedirectStandardError "$stdoutLog.err" `
    -ArgumentList @($project, '-game', '-nullrhi', '-nosplash', '-unattended', '-AI003WorldSmoke', '-log')

if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
    $process.Kill()
    throw "UE5 world smoke test timed out after $TimeoutSeconds s; see $ueLog"
}

$marker = $null
if (Test-Path $ueLog) {
    $marker = Select-String -Path $ueLog -Pattern '\[AI003\]\[WORLD\] (PASS|FAIL)' -ErrorAction SilentlyContinue |
        Select-Object -Last 1
}
if ($marker) { Write-Host $marker.Line }

if (-not $marker -or $marker.Line -notmatch 'PASS') {
    if (Test-Path $ueLog) {
        Write-Host '--- last log lines ---'
        Select-String -Path $ueLog -Pattern '\[AI003\]' | Select-Object -Last 10 | ForEach-Object { $_.Line }
    }
    throw "UE5 world smoke test failed; see $ueLog"
}

Write-Host 'UE5 world smoke test passed.' -ForegroundColor Green
