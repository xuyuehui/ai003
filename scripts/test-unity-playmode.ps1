#!/usr/bin/env pwsh
# Runs the Unity PlayMode test that exercises ConnectionBootstrap exactly like
# pressing Play does. Requires the gateway to be running.
[CmdletBinding()]
param(
    [string]$Unity = 'C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe',
    [int]$TimeoutMinutes = 10
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'clients\unity\UnityGameClient'
$results = Join-Path $root 'artifacts\playmode-results.xml'
$log = Join-Path $root 'artifacts\logs\unity-playmode.log'

if (-not (Test-Path $Unity)) { throw "Unity editor not found: $Unity" }
New-Item -ItemType Directory -Force -Path (Split-Path $results) | Out-Null
Remove-Item $results -Force -ErrorAction SilentlyContinue

Write-Host '==> Unity PlayMode test (ConnectionBootstrap)' -ForegroundColor Cyan
$process = Start-Process -FilePath $Unity -PassThru -ArgumentList @(
    '-batchmode', '-nographics',
    '-projectPath', $project,
    '-runTests', '-testPlatform', 'PlayMode',
    '-testResults', $results,
    '-logFile', $log
)

if (-not $process.WaitForExit($TimeoutMinutes * 60000)) {
    $process.Kill()
    throw "Unity PlayMode tests timed out after $TimeoutMinutes min; see $log"
}

if (-not (Test-Path $results)) { throw "Unity produced no test results; see $log" }

[xml]$xml = Get-Content $results -Raw
$run = $xml.'test-run'
Write-Host ("tests: total={0} passed={1} failed={2} result={3}" -f $run.total, $run.passed, $run.failed, $run.result)

if ($run.result -ne 'Passed') {
    Select-String -Path $results -Pattern 'result="Failed"' | ForEach-Object { $_.Line }
    throw "Unity PlayMode tests failed; see $results"
}

Write-Host 'Unity PlayMode test passed.' -ForegroundColor Green
