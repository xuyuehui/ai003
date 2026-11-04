param(
    [int]$Seconds = 8,
    [string]$BaseUrl = "http://127.0.0.1:8080"
)

$ErrorActionPreference = 'Stop'

try {
    Invoke-RestMethod "$BaseUrl/healthz" -TimeoutSec 5 | Out-Null
}
catch {
    Write-Host "server not reachable at $BaseUrl" -ForegroundColor Red
    Write-Host "start it first: scripts\build-server.cmd ; server\bin\ai003-server.exe" -ForegroundColor Yellow
    exit 1
}

$repo = Split-Path $PSScriptRoot -Parent
$serverDir = Join-Path $repo 'server'
$work = Join-Path $env:TEMP 'ai003-demo'
New-Item -ItemType Directory -Force -Path $work | Out-Null

$alphaOut = Join-Path $work 'alpha.json'
$betaOut = Join-Path $work 'beta.json'
$alphaErr = Join-Path $work 'alpha.err'
$betaErr = Join-Path $work 'beta.err'

Write-Host "==> technical demo: two headless clients in one arena (${Seconds}s)"
$alpha = Start-Process -FilePath 'go' -PassThru -NoNewWindow -WorkingDirectory $serverDir `
    -RedirectStandardOutput $alphaOut -RedirectStandardError $alphaErr `
    -ArgumentList @('run', './cmd/democlient', '-seconds', "$Seconds", '-name', 'alpha', '-clientId', 'demo-alpha')
$beta = Start-Process -FilePath 'go' -PassThru -NoNewWindow -WorkingDirectory $serverDir `
    -RedirectStandardOutput $betaOut -RedirectStandardError $betaErr `
    -ArgumentList @('run', './cmd/democlient', '-seconds', "$Seconds", '-name', 'beta', '-clientId', 'demo-beta', '-still')

if (-not $alpha.WaitForExit(120000)) { $alpha.Kill(); throw 'alpha timed out' }
if (-not $beta.WaitForExit(120000)) { $beta.Kill(); throw 'beta timed out' }

Write-Host "`n-- alpha --"
Get-Content $alphaOut -Raw
Write-Host "-- beta --"
Get-Content $betaOut -Raw

$alphaJson = Get-Content $alphaOut -Raw | ConvertFrom-Json
$betaJson = Get-Content $betaOut -Raw | ConvertFrom-Json

$sameRoom = $alphaJson.roomId -eq $betaJson.roomId
$framesOk = $alphaJson.frames -ge ($Seconds * 15)
$tickOk = $alphaJson.maxTickGap -le 2 -and $betaJson.maxTickGap -le 2

Write-Host ""
Write-Host ("checks: sameRoom={0}  framesOk={1}  tickGapOk={2}" -f $sameRoom, $framesOk, $tickOk)
if (-not ($sameRoom -and $framesOk -and $tickOk)) { exit 1 }

Write-Host "`n-- server counters --"
& (Join-Path $PSScriptRoot 'demo-status.ps1') -BaseUrl $BaseUrl
