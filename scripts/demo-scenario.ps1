param(
    [int]$Seconds = 8,
    [string]$BaseUrl = "",
    [string]$Url = "",
    [string]$Room = "",
    [switch]$UpdateBaseline,
    [string]$BaselinePath = ""
)

$ErrorActionPreference = 'Stop'

# --- gateway address ---------------------------------------------------------
# $Url (ws://host:port/ws) is what the headless clients dial; $BaseUrl is only
# used for the /healthz and /demo probes. The clients used to be hard-wired to
# ws://127.0.0.1:8080/ws, so passing -BaseUrl alone health-checked one gateway
# while the traffic went to another (usually the user's long-running one).
if ([string]::IsNullOrWhiteSpace($BaseUrl)) { $BaseUrl = 'http://127.0.0.1:8080' }

if ([string]::IsNullOrWhiteSpace($Url)) {
    $Url = ($BaseUrl -replace '^http', 'ws').TrimEnd('/')
}
elseif (-not $PSBoundParameters.ContainsKey('BaseUrl')) {
    $BaseUrl = (($Url -replace '^ws', 'http') -replace '/ws$', '').TrimEnd('/')
}
if ($Url -notmatch '/ws$') { $Url = "$Url/ws" }

try {
    Invoke-RestMethod "$BaseUrl/healthz" -TimeoutSec 5 | Out-Null
}
catch {
    Write-Host "server not reachable at $BaseUrl" -ForegroundColor Red
    Write-Host "start it first: scripts\build-server.cmd ; server\bin\ai003-server.exe" -ForegroundColor Yellow
    exit 1
}

$repo = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($BaselinePath)) {
    $BaselinePath = Join-Path $repo 'demo\baseline.json'
}
if ([string]::IsNullOrWhiteSpace($Room)) {
    # A fresh room per run keeps spawn slots (0,0)/(4000,0) deterministic.
    $Room = 'demo-' + (Get-Date -Format 'MMddHHmmss')
}

$serverDir = Join-Path $repo 'server'
$work = Join-Path $env:TEMP 'ai003-demo'
New-Item -ItemType Directory -Force -Path $work | Out-Null

$alphaOut = Join-Path $work 'alpha.json'
$betaOut = Join-Path $work 'beta.json'
$alphaErr = Join-Path $work 'alpha.err'
$betaErr = Join-Path $work 'beta.err'

Write-Host "==> technical demo: two headless clients in ${Room} (${Seconds}s)"
$alpha = Start-Process -FilePath 'go' -PassThru -NoNewWindow -WorkingDirectory $serverDir `
    -RedirectStandardOutput $alphaOut -RedirectStandardError $alphaErr `
    -ArgumentList @('run', './cmd/democlient', '-url', $Url, '-seconds', "$Seconds", '-name', 'alpha',
        '-clientId', "$Room-alpha", '-room', $Room, '-spar')
$beta = Start-Process -FilePath 'go' -PassThru -NoNewWindow -WorkingDirectory $serverDir `
    -RedirectStandardOutput $betaOut -RedirectStandardError $betaErr `
    -ArgumentList @('run', './cmd/democlient', '-url', $Url, '-seconds', "$Seconds", '-name', 'beta',
        '-clientId', "$Room-beta", '-room', $Room, '-still')

if (-not $alpha.WaitForExit(120000)) { $alpha.Kill(); throw 'alpha timed out' }
if (-not $beta.WaitForExit(120000)) { $beta.Kill(); throw 'beta timed out' }

Write-Host "`n-- alpha --"
Get-Content $alphaOut -Raw
Write-Host "-- beta --"
Get-Content $betaOut -Raw

$alphaJson = Get-Content $alphaOut -Raw | ConvertFrom-Json
$betaJson = Get-Content $betaOut -Raw | ConvertFrom-Json
$demo = Invoke-RestMethod "$BaseUrl/demo" -TimeoutSec 5

$sameRoom = $alphaJson.roomId -eq $betaJson.roomId
$framesOk = $alphaJson.frames -ge ($Seconds * 15)
$tickOk = $alphaJson.maxTickGap -le 2 -and $betaJson.maxTickGap -le 2

$metrics = [ordered]@{
    seconds     = $Seconds
    framesAlpha = [int]$alphaJson.frames
    framesBeta  = [int]$betaJson.frames
    maxTickGap  = [int]([Math]::Max($alphaJson.maxTickGap, $betaJson.maxTickGap))
    attack      = [int]([Math]::Max($alphaJson.events.attack, $betaJson.events.attack))
    hit         = [int]([Math]::Max($alphaJson.events.hit, $betaJson.events.hit))
}

Write-Host ""
Write-Host ("checks: sameRoom={0}  framesOk={1}  tickGapOk={2}" -f $sameRoom, $framesOk, $tickOk)
if (-not ($sameRoom -and $framesOk -and $tickOk)) { exit 1 }

if ($UpdateBaseline) {
    New-Item -ItemType Directory -Force -Path (Split-Path $BaselinePath) | Out-Null
    $metrics | ConvertTo-Json | Set-Content -Path $BaselinePath -Encoding utf8
    Write-Host "baseline written: $BaselinePath"
}
elseif (Test-Path $BaselinePath) {
    $baseline = Get-Content $BaselinePath -Raw | ConvertFrom-Json
    $problems = @()

    if ($metrics.maxTickGap -gt 2) {
        $problems += "maxTickGap $($metrics.maxTickGap) > 2"
    }
    $baselineFramesPerSecond = $baseline.framesAlpha / [Math]::Max(1, [int]$baseline.seconds)
    $currentFramesPerSecond = $metrics.framesAlpha / [Math]::Max(1, [int]$metrics.seconds)
    if ($currentFramesPerSecond -lt $baselineFramesPerSecond * 0.75) {
        $problems += ("frames/s {0:N1} < 75% of baseline {1:N1}" -f $currentFramesPerSecond, $baselineFramesPerSecond)
    }
    $baselineAttackRate = $baseline.attack / [Math]::Max(1, [int]$baseline.seconds)
    $currentAttackRate = $metrics.attack / [Math]::Max(1, [int]$metrics.seconds)
    if ($currentAttackRate -lt $baselineAttackRate * 0.5) {
        $problems += ("attacks/s {0:N2} < 50% of baseline {1:N2}" -f $currentAttackRate, $baselineAttackRate)
    }
    $baselineHitRate = $baseline.hit / [Math]::Max(1, [int]$baseline.seconds)
    $currentHitRate = $metrics.hit / [Math]::Max(1, [int]$metrics.seconds)
    if ($currentHitRate -lt $baselineHitRate * 0.5) {
        $problems += ("hits/s {0:N2} < 50% of baseline {1:N2}" -f $currentHitRate, $baselineHitRate)
    }

    Write-Host ("baseline: frames {0}, maxTickGap {1}, attack {2}, hit {3}" -f `
        $baseline.framesAlpha, $baseline.maxTickGap, $baseline.attack, $baseline.hit)
    if ($problems.Count -gt 0) {
        Write-Host "REGRESSIONS:" -ForegroundColor Red
        foreach ($problem in $problems) { Write-Host "  - $problem" -ForegroundColor Red }
        exit 1
    }
    Write-Host "metrics within baseline tolerances" -ForegroundColor Green
}
else {
    Write-Host "no baseline yet; create one with -UpdateBaseline" -ForegroundColor Yellow
}

Write-Host "`n-- server counters --"
& (Join-Path $PSScriptRoot 'demo-status.ps1') -BaseUrl $BaseUrl
