#!/usr/bin/env pwsh
# Headless demonstration of the Quake III style arena room: usercmds,
# sequenced snapshots, delta compression and acks. No Unity/UE required.
[CmdletBinding()]
param(
    [string]$Url = 'ws://127.0.0.1:8080/ws',
    [double]$Seconds = 4
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$serverExe = Join-Path $root 'server\bin\ai003-server.exe'
$artifacts = Join-Path $root 'artifacts\demo'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$health = ($Url -replace '^ws', 'http') -replace '/ws$', '/healthz'
$started = $false
try { Invoke-RestMethod -Uri $health -TimeoutSec 2 | Out-Null }
catch {
    if (-not (Test-Path $serverExe)) { throw "missing $serverExe; run scripts/build-server.cmd" }
    Start-Process -FilePath $serverExe -ArgumentList '-addr', '127.0.0.1:8080', '-world', (Join-Path $root 'world') -WindowStyle Hidden | Out-Null
    $started = $true
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        try { Invoke-RestMethod -Uri $health -TimeoutSec 1 | Out-Null; break } catch { }
    }
}

try {
    Push-Location (Join-Path $root 'server')
    try {
        $raw = go run ./cmd/arenaclient -url $Url -seconds $Seconds 2>$null
        $code = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($code -ne 0) { throw "arena client failed (exit $code): $raw" }
    $metrics = $raw | ConvertFrom-Json
    $metrics | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $artifacts 'arena.json') -Encoding UTF8
    Write-Host ("arena room {0}: snapshots={1} deltas={2} acks={3} origin={4}/{5}/{6} moved={7}" -f `
            $metrics.roomId, $metrics.snapshots, $metrics.deltas, $metrics.ack,
            $metrics.origin[0], $metrics.origin[1], $metrics.origin[2], $metrics.moved)
    if (-not $metrics.moved) { throw 'arena client did not move' }
}
finally {
    if ($started) {
        $conn = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -eq 8080 } | Select-Object -First 1
        if ($conn) { taskkill /PID $conn.OwningProcess /T /F 2>&1 | Out-Null }
    }
}
