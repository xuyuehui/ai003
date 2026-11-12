#!/usr/bin/env pwsh
# One-command environment verification:
#   Go unit tests -> gateway up -> Go ping client -> Unity smoke.
# UE5 is out of scope: its steps are opt-in (-WithUE / -WithUEWorld).
[CmdletBinding()]
param(
    [string]$Url = 'ws://127.0.0.1:8080/ws',
    [string]$Unity = 'C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe',
    [string]$Engine = 'C:\Program Files\Epic Games\UE_5.8',
    [switch]$SkipUnity,
    [switch]$SkipPlayMode,
    # UE5 is no longer part of the project; these stay for archived runs.
    [switch]$WithUE,
    [switch]$WithUEWorld,
    [switch]$SkipGoTests,
    [switch]$SkipDemo,
    [switch]$WithCompare,
    [switch]$WithRoom
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

    if (-not $SkipDemo) {
        Write-Host '==> Demo scenario (headless clients + metrics baseline)' -ForegroundColor Cyan
        $baseUrl = ($Url -replace '^ws', 'http') -replace '/ws$', ''
        & (Join-Path $scripts 'demo-scenario.ps1') -Seconds 6 -BaseUrl $baseUrl
        if ($LASTEXITCODE -ne 0) { throw 'demo scenario failed' }
        $results['demo'] = 'PASS'
    }
    else { $results['demo'] = 'SKIP' }

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

    if ($WithUE) {
        & (Join-Path $scripts 'smoke-ue5.ps1') -Engine $Engine -Url $Url
        $results['ue5'] = 'PASS'

        if ($WithUEWorld) {
            & (Join-Path $scripts 'smoke-ue5-world.ps1') -Engine $Engine
            $results['ue5-world'] = 'PASS'
        }
        else { $results['ue5-world'] = 'SKIP' }
    }
    else {
        $results['ue5'] = 'SKIP'
        $results['ue5-world'] = 'SKIP'
    }

    if ($WithCompare) {
        Write-Host '==> Demo compare (Unity vs UE5 fixed camera)' -ForegroundColor Cyan
        & (Join-Path $scripts 'demo-compare.ps1') -MaxBrightnessDelta 8 -MinWithin24 80
        if ($LASTEXITCODE -ne 0) { throw 'demo compare failed' }
        $results['compare'] = 'PASS'
    }

    if ($WithRoom) {
        Write-Host '==> Demo room (Unity + UE bot in one room)' -ForegroundColor Cyan
        $roomPath = Join-Path (Join-Path $root 'artifacts\demo') 'room.json'
        $roomOk = $false
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            if (Test-Path $roomPath) { Remove-Item $roomPath -Force }
            & (Join-Path $scripts 'demo-room.ps1') -Seconds 4 -NoPublish
            $roomPassed = $false
            if (Test-Path $roomPath) {
                $roomPassed = (Get-Content $roomPath -Raw | ConvertFrom-Json).passed
            }
            if ($LASTEXITCODE -eq 0 -and $roomPassed) { $roomOk = $true; break }
            if ($attempt -lt 2) { Write-Host 'room had no combat; retrying once' -ForegroundColor Yellow }
        }
        if (-not $roomOk) { throw 'demo room failed' }
        $results['room'] = 'PASS'
    }

    # --- metrics + regression baseline -----------------------------------
    $artifacts = Join-Path $root 'artifacts\demo'
    $compareMetrics = $null
    $roomMetrics = $null
    if ($WithCompare -and (Test-Path (Join-Path $artifacts 'compare.json'))) {
        $compareMetrics = Get-Content (Join-Path $artifacts 'compare.json') -Raw | ConvertFrom-Json
    }
    if ($WithRoom -and (Test-Path (Join-Path $artifacts 'room.json'))) {
        $roomMetrics = Get-Content (Join-Path $artifacts 'room.json') -Raw | ConvertFrom-Json
    }

    $report = [ordered]@{
        generatedAt = (Get-Date).ToString('o')
        results     = $results
        compare     = $compareMetrics
        room        = $roomMetrics
    }
    $reportPath = Join-Path $artifacts 'smoke-report.json'
    New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $reportPath -Encoding UTF8

    $baselinePath = Join-Path $root 'demo\smoke-baseline.json'
    if ($UpdateBaseline -and $compareMetrics -and $roomMetrics) {
        New-Item -ItemType Directory -Force -Path (Split-Path $baselinePath -Parent) | Out-Null
        [ordered]@{
            generatedAt     = (Get-Date).ToString('o')
            brightnessDelta = $compareMetrics.brightnessDelta
            meanDiff        = $compareMetrics.meanDiff
            within24        = $compareMetrics.within24
            roomHitDelta    = $roomMetrics.hitDelta
            roomAttackDelta = $roomMetrics.attackDelta
        } | ConvertTo-Json | Set-Content -Path $baselinePath -Encoding UTF8
        Write-Host "baseline updated: $baselinePath" -ForegroundColor Cyan
    }
    elseif ((Test-Path $baselinePath) -and $compareMetrics -and $roomMetrics) {
        $baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
        $regressions = @()
        if ($compareMetrics.brightnessDelta -gt ($baseline.brightnessDelta + 1.0)) {
            $regressions += ("brightnessDelta {0} > {1}+1" -f $compareMetrics.brightnessDelta, $baseline.brightnessDelta)
        }
        if ($compareMetrics.within24 -lt ($baseline.within24 - 1.0)) {
            $regressions += ("within24 {0} < {1}-1" -f $compareMetrics.within24, $baseline.within24)
        }
        if ($compareMetrics.meanDiff -gt ($baseline.meanDiff + 1.0)) {
            $regressions += ("meanDiff {0} > {1}+1" -f $compareMetrics.meanDiff, $baseline.meanDiff)
        }
        if ($roomMetrics.hitDelta -lt 1) { $regressions += 'room hitDelta 0' }
        if ($regressions.Count -gt 0) {
            throw ("regressions: " + ($regressions -join '; '))
        }
        Write-Host "baseline ok: $baselinePath" -ForegroundColor Green
    }
    Write-Host "smoke report: $reportPath" -ForegroundColor Cyan
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
