#!/usr/bin/env pwsh
# One-command environment verification:
#   Go unit tests -> gateway up -> Go ping client -> Unity smoke -> UE5 smoke.
[CmdletBinding()]
param(
    [string]$Url = 'ws://127.0.0.1:8080/ws',
    [string]$Unity = 'C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe',
    [string]$Engine = 'C:\Program Files\Epic Games\UE_5.8',
    [switch]$SkipUnity,
    [switch]$SkipPlayMode,
    [switch]$SkipUE,
    [switch]$SkipGoTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scripts = $PSScriptRoot
$serverExe = Join-Path $root 'server\bin\ai003-server.exe'
$results = [ordered]@{}

function Get-HealthUrl([string]$wsUrl) {
    $health = $wsUrl -replace '^ws', 'http'
    return $health -replace '/ws$', '/healthz'
}

function Test-Gateway([string]$wsUrl) {
    try {
        $response = Invoke-RestMethod -Uri (Get-HealthUrl $wsUrl) -TimeoutSec 2
        return $response.status -eq 'ok'
    }
    catch { return $false }
}

if (-not (Test-Path $serverExe)) {
    & (Join-Path $scripts 'build-server.ps1')
}

if (-not $SkipGoTests) {
    Write-Host '==> Go unit tests' -ForegroundColor Cyan
    Push-Location (Join-Path $root 'server')
    try {
        go test ./... -count=1
        if ($LASTEXITCODE -ne 0) { throw 'Go tests failed' }
        $results['go-tests'] = 'PASS'
    }
    finally { Pop-Location }
}

$serverProcess = $null
if (-not (Test-Gateway $Url)) {
    Write-Host '==> Starting gateway on 127.0.0.1:8080' -ForegroundColor Cyan
    $serverProcess = Start-Process -FilePath $serverExe -ArgumentList '-addr', '127.0.0.1:8080' -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 40 -and -not (Test-Gateway $Url); $i++) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Gateway $Url)) { throw 'gateway did not become healthy' }
}
else {
    Write-Host '==> Gateway already running' -ForegroundColor Cyan
}

try {
    Write-Host '==> Go ping client' -ForegroundColor Cyan
    Push-Location (Join-Path $root 'server')
    try {
        go run ./cmd/pingclient -url $Url -n 3
        if ($LASTEXITCODE -ne 0) { throw 'Go ping client failed' }
        $results['go-client'] = 'PASS'
    }
    finally { Pop-Location }

    if (-not $SkipUnity) {
        & (Join-Path $scripts 'smoke-unity.ps1') -Unity $Unity -Url $Url
        $results['unity'] = 'PASS'

        if (-not $SkipPlayMode) {
            & (Join-Path $scripts 'test-unity-playmode.ps1') -Unity $Unity
            $results['unity-pm'] = 'PASS'
        }
        else { $results['unity-pm'] = 'SKIP' }
    }
    else {
        $results['unity'] = 'SKIP'
        $results['unity-pm'] = 'SKIP'
    }

    if (-not $SkipUE) {
        & (Join-Path $scripts 'smoke-ue5.ps1') -Engine $Engine -Url $Url
        $results['ue5'] = 'PASS'
    }
    else { $results['ue5'] = 'SKIP' }
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Write-Host '==> Stopping gateway' -ForegroundColor Cyan
        $serverProcess.Kill()
        $serverProcess.WaitForExit(5000) | Out-Null
    }
}

Write-Host ''
Write-Host '===== AI003 environment verification =====' -ForegroundColor Green
foreach ($key in $results.Keys) { '{0,-10} {1}' -f $key, $results[$key] }
