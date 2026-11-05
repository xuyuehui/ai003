param(
    [switch]$SkipScenario,
    [switch]$SkipCompare,
    [switch]$SkipRoom,
    [switch]$NoPublish,
    [switch]$KeepGateway,
    [switch]$UpdateBaseline,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repo 'artifacts\demo'
$logs = Join-Path $repo 'artifacts\logs'
$baselinePath = Join-Path $repo 'demo\smoke-baseline.json'
$baseUrl = 'http://127.0.0.1:8080'

New-Item -ItemType Directory -Force -Path $artifacts, $logs | Out-Null

if (-not $SkipBuild) {
    Write-Host '==> building server' -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-server.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'server build failed' }
}

function Test-Gateway {
    try {
        $health = Invoke-RestMethod "$baseUrl/healthz" -TimeoutSec 2
        return $health.status -eq 'ok'
    }
    catch { return $false }
}

$gatewayStarted = $false
if (-not (Test-Gateway)) {
    $serverExe = Join-Path $repo 'server\bin\ai003-server.exe'
    if (-not (Test-Path $serverExe)) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-server.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'server build failed' }
    }
    Write-Host '==> starting gateway' -ForegroundColor Cyan
    Start-Process -FilePath $serverExe -ArgumentList @('-addr', '127.0.0.1:8080', '-world', (Join-Path $repo 'world')) `
        -WorkingDirectory $repo -NoNewWindow `
        -RedirectStandardOutput (Join-Path $logs 'hub-gateway.log') `
        -RedirectStandardError (Join-Path $logs 'hub-gateway.err') | Out-Null
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and -not (Test-Gateway)) { Start-Sleep -Seconds 1 }
    if (-not (Test-Gateway)) { throw 'gateway did not become healthy' }
    $gatewayStarted = $true
}

$steps = New-Object System.Collections.Generic.List[object]

function Invoke-DemoStep {
    param([string]$Name, [string]$ScriptFile, [string[]]$Arguments)

    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $ok = $true
    $errorText = ''
    try {
        $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $ScriptFile)) + $Arguments
        & powershell @argumentList
        if ($LASTEXITCODE -ne 0) { throw "exit code $LASTEXITCODE" }
    }
    catch {
        $ok = $false
        $errorText = "$_"
        Write-Host "FAIL $Name : $errorText" -ForegroundColor Red
    }
    $watch.Stop()
    $steps.Add([pscustomobject]@{
            name    = $Name
            ok      = $ok
            seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
            error   = $errorText
        })
    return $ok
}

if (-not $SkipScenario) {
    $scenarioArgs = @()
    if ($NoPublish) { $scenarioArgs += '-NoPublish' }
    [void](Invoke-DemoStep -Name 'demo-scenario' -ScriptFile 'demo-scenario.ps1' -Arguments $scenarioArgs)
}

if (-not $SkipCompare) {
    $compareArgs = @()
    if ($NoPublish) { $compareArgs += '-NoPublish' }
    [void](Invoke-DemoStep -Name 'demo-compare' -ScriptFile 'demo-compare.ps1' -Arguments $compareArgs)
}

$roomRetryOk = $false
if (-not $SkipRoom) {
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $roomProbe = Join-Path $artifacts 'room.json'
        if (Test-Path $roomProbe) { Remove-Item $roomProbe -Force }
        $label = if ($attempt -eq 1) { 'demo-room' } else { 'demo-room (retry)' }
        $stepOk = Invoke-DemoStep -Name $label -ScriptFile 'demo-room.ps1' -Arguments @('-Seconds', '4')
        $roomPassed = $false
        if (Test-Path $roomProbe) {
            $roomPassed = (Get-Content $roomProbe -Raw | ConvertFrom-Json).passed
        }
        if ($stepOk -and $roomPassed) { $roomRetryOk = $true; break }
        if ($attempt -lt 2) { Write-Host 'room had no combat; retrying once' -ForegroundColor Yellow }
    }
    if (-not $roomRetryOk) { Write-Host 'demo-room failed after retry' -ForegroundColor Red }
}

# --- collect ---------------------------------------------------------------
$compareMetrics = $null
$comparePath = Join-Path $artifacts 'compare.json'
if (Test-Path $comparePath) { $compareMetrics = Get-Content $comparePath -Raw | ConvertFrom-Json }

$roomMetrics = $null
$roomPath = Join-Path $artifacts 'room.json'
if (Test-Path $roomPath) { $roomMetrics = Get-Content $roomPath -Raw | ConvertFrom-Json }

$demo = $null
$world = $null
try { $demo = Invoke-RestMethod "$baseUrl/demo" -TimeoutSec 5 } catch { }
try { $world = Invoke-RestMethod "$baseUrl/world" -TimeoutSec 5 } catch { }

$failed = @($steps | Where-Object {
        if (-not $_.ok -and $roomRetryOk -and $_.name -like 'demo-room*') { return $false }
        return -not $_.ok
    })
if ($compareMetrics -and -not $compareMetrics.passed) { $failed += 'compare-metrics' }
if ($roomMetrics -and -not $roomMetrics.passed) { $failed += 'room-metrics' }
if (-not $compareMetrics -and -not $SkipCompare) { $failed += 'compare-missing' }
if (-not $roomMetrics -and -not $SkipRoom) { $failed += 'room-missing' }

$report = @{}
$report['generatedAt'] = (Get-Date).ToString('o')
$report['steps'] = $steps.ToArray()
$report['compare'] = $compareMetrics
$report['room'] = $roomMetrics
$report['demo'] = $demo
$report['world'] = $world
$report['passed'] = ($failed.Count -eq 0)
$report['failed'] = @($failed)
$reportJson = Join-Path $artifacts 'report.json'
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $reportJson -Encoding UTF8

# --- baseline --------------------------------------------------------------
$baselineNote = 'no baseline'
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
    $baselineNote = 'baseline updated'
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
        $failed += $regressions
        $report['failed'] = @($failed)
        $report['passed'] = $false
        $report | ConvertTo-Json -Depth 8 | Set-Content -Path $reportJson -Encoding UTF8
        $baselineNote = 'REGRESSED: ' + ($regressions -join '; ')
    }
    else { $baselineNote = 'baseline ok' }
}

# --- markdown --------------------------------------------------------------
$compareLine = if ($compareMetrics) {
    ("| 指标 | Unity | UE | 结果 |`n| --- | --- | --- | --- |`n| 亮度 | {0} | {1} | Δ {2} |`n| 平均像素差 | - | - | {3}/255 |`n| 24 内像素 | - | - | {4}% |" -f `
        $compareMetrics.unityBrightness, $compareMetrics.ueBrightness, $compareMetrics.brightnessDelta,
        $compareMetrics.meanDiff, $compareMetrics.within24)
}
else { '_未运行 demo-compare_' }

$roomLine = if ($roomMetrics) {
    ("| 房间 | 玩家 | tick | 帧 | attack | hit |`n| --- | --- | --- | --- | --- | --- |`n| {0} | {1} | {2} | {3} | {4} | {5} |" -f `
        $roomMetrics.roomId, $roomMetrics.players, $roomMetrics.tickDelta, $roomMetrics.frameDelta,
        $roomMetrics.attackDelta, $roomMetrics.hitDelta)
}
else { '_未运行 demo-room_' }


$stepLines = ($steps | ForEach-Object {
        $status = if ($_.ok) { 'PASS' } else { 'FAIL' }
        "| {0} | {1} | {2} s | {3} |" -f $_.name, $status, $_.seconds, $_.error
    }) -join "`n"

$markdown = @(
    '# AI003 演示报告'
    ''
    ('生成时间：' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
    ('总体：' + $(if ($report.passed) { 'PASS' } else { 'FAIL' }))
    ''
    '## 步骤'
    ''
    '| 步骤 | 结果 | 耗时 | 备注 |'
    '| --- | --- | --- | --- |'
    $stepLines
    ''
    '## 视觉对比'
    ''
    $compareLine
    ''
    '## 房间战斗'
    ''
    $roomLine
    ''
    '## 服务端'
    ''
    ('- 房间：' + (@($demo.rooms).Count) + ' 个')
    ('- 世界：' + $world.world.sizeMeters + ' m / ' + $world.world.resolution + ' 顶点')
    ('- 基线：' + $baselineNote)
    ''
    '图片：compare.png、diff.png、room-compare.png（同目录）'
) -join "`n"

$reportMd = Join-Path $artifacts 'report.md'
Set-Content -Path $reportMd -Value $markdown -Encoding UTF8

Write-Host ''
Write-Host '--- demo hub ---'
foreach ($step in $steps) {
    $status = if ($step.ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0,-16} {1}  {2} s" -f $step.name, $status, $step.seconds)
}
if ($compareMetrics) {
    Write-Host ("compare: brightness Δ{0}, mean {1}/255, within24 {2}%" -f `
        $compareMetrics.brightnessDelta, $compareMetrics.meanDiff, $compareMetrics.within24)
}
if ($roomMetrics) {
    Write-Host ("room: tick +{0}, attack {1}, hit {2}, players {3}" -f `
        $roomMetrics.tickDelta, $roomMetrics.attackDelta, $roomMetrics.hitDelta, $roomMetrics.players)
}
Write-Host "baseline: $baselineNote"
Write-Host "report:   $reportMd"
Write-Host "json:     $reportJson"

if ($gatewayStarted -and -not $KeepGateway) {
    $conn = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -eq 8080 } | Select-Object -First 1
    if ($conn) { taskkill /PID $conn.OwningProcess /T /F 2>&1 | Out-Null }
}

if ($failed.Count -gt 0) {
    Write-Host ("demo hub failed: " + ($failed -join '; ')) -ForegroundColor Red
    exit 1
}
Write-Host 'demo hub passed' -ForegroundColor Green
