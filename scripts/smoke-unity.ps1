#!/usr/bin/env pwsh
# Runs the Unity client's headless smoke test against the gateway.
#
# Unity.exe is a GUI-subsystem binary, so `&` returns immediately; we start it
# with Start-Process and wait for the editor to exit. The pass/fail verdict is
# read from the log marker because a batchmode editor always exits 0.
[CmdletBinding()]
param(
    [string]$Unity = 'C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe',
    [string]$Url = 'ws://127.0.0.1:8080/ws',
    [int]$TimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'clients\unity\UnityGameClient'
$log = Join-Path $root 'artifacts\logs\unity-smoke.log'

if (-not (Test-Path $Unity)) { throw "Unity editor not found: $Unity" }
New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
Remove-Item $log -Force -ErrorAction SilentlyContinue

$env:AI003_WS_URL = $Url
Write-Host "==> Unity smoke test ($Url)" -ForegroundColor Cyan
$process = Start-Process -FilePath $Unity -PassThru -ArgumentList @(
    '-batchmode', '-nographics',
    '-projectPath', $project,
    '-executeMethod', 'AI003.EditorTools.SmokeTest.Run',
    '-quit',
    '-logFile', $log
)

if (-not $process.WaitForExit($TimeoutMinutes * 60000)) {
    $process.Kill()
    throw "Unity smoke test timed out after $TimeoutMinutes min; see $log"
}

$result = $null
if (Test-Path $log) {
    $result = Select-String -Path $log -Pattern '\[AI003\]\[SMOKE\] (PASS|FAIL)' -ErrorAction SilentlyContinue |
        Select-Object -Last 1
}
if (-not $result) { throw "Unity smoke test produced no verdict; see $log" }

Write-Host $result.Line
if ($result.Line -notmatch 'PASS') { throw "Unity smoke test failed; see $log" }
Write-Host 'Unity smoke test passed.' -ForegroundColor Green
