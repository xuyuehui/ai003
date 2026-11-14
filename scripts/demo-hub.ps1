param(
    [switch]$SkipScenario,
    [switch]$SkipCompare,
    [switch]$SkipRoom,
    [switch]$NoPublish,
    [switch]$KeepGateway,
    [switch]$UpdateBaseline,
    [switch]$SkipBuild,
    [string]$Url = '',
    [int]$Port = 8080,
    [switch]$WithUE,
    [switch]$SkipUE
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repo 'artifacts\demo'
$logs = Join-Path $repo 'artifacts\logs'
$baselinePath = Join-Path $repo 'demo\smoke-baseline.json'

. (Join-Path $PSScriptRoot '_ports.ps1')
if ([string]::IsNullOrWhiteSpace($Url)) { $Url = ConvertTo-WsUrl "http://127.0.0.1:$Port" }
$baseUrl = ConvertTo-BaseUrl $Url
# Children that do not take -Url (demo-compare drives the Unity player) still
# read AI003_WS_URL; exporting it here keeps every step on the same gateway.
$env:AI003_WS_URL = $Url

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
$gatewayPid = 0
if (-not (Test-Gateway)) {
    $serverExe = Join-Path $repo 'server\bin\ai003-server.exe'
    if (-not (Test-Path $serverExe)) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build-server.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'server build failed' }
    }
    Write-Host '==> starting gateway' -ForegroundColor Cyan
    $gatewayProcess = Start-Process -FilePath $serverExe -ArgumentList @('-addr', "127.0.0.1:$Port", '-world', (Join-Path $repo 'world')) `
        -WorkingDirectory $repo -NoNewWindow `
        -RedirectStandardOutput (Join-Path $logs 'hub-gateway.log') `
        -RedirectStandardError (Join-Path $logs 'hub-gateway.err') -PassThru
    $gatewayPid = [int]$gatewayProcess.Id
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and -not (Test-Gateway)) { Start-Sleep -Seconds 1 }
    if (-not (Test-Gateway)) { throw 'gateway did not become healthy' }
    # The PID that owns the port is the only one Stop-PortListener may kill; if it
    # differs from the process we launched, the cleanup must not touch the port.
    $listenerPid = Get-ListeningPid -Port $Port
    if ($null -ne $listenerPid) { $gatewayPid = $listenerPid }
    $gatewayStarted = $true
}

$steps = New-Object System.Collections.Generic.List[object]

function Invoke-DemoStep {
    param([string]$Name, [string]$ScriptFile, [string[]]$Arguments)

    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $ok = $true
    $degradedStep = $false
    $errorText = ''
    try {
        $argumentList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $ScriptFile)) + $Arguments
        & powershell @argumentList
        $exitCode = $LASTEXITCODE
        if ($exitCode -eq 2) {
            # Child ran and its assertions held, but it declared itself DEGRADED
            # (e.g. demo-room without a UE client). Not a pass, not a hard failure.
            $degradedStep = $true
            $errorText = 'DEGRADED: 未经双端验证'
            Write-Host "DEGRADED $Name : $($errorText)" -ForegroundColor Yellow
        }
        elseif ($exitCode -ne 0) { throw "exit code $exitCode" }
    }
    catch {
        $ok = $false
        $errorText = "$_"
        Write-Host "FAIL $Name : $errorText" -ForegroundColor Red
    }
    $watch.Stop()
    $steps.Add([pscustomobject]@{
            name     = $Name
            ok       = $ok
            degraded = $degradedStep
            seconds  = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
            error    = $errorText
        })
    return $ok
}

if (-not $SkipScenario) {
    $scenarioArgs = @('-Url', $Url)
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
        $roomStepArgs = @('-Seconds', '4', '-Url', $Url)
        if ($WithUE) { $roomStepArgs += '-WithUE' }
        if ($SkipUE) { $roomStepArgs += '-SkipUE' }
        $stepOk = Invoke-DemoStep -Name $label -ScriptFile 'demo-room.ps1' -Arguments $roomStepArgs
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

# Single-end is the normal default coverage (Unity only), so it is not a failure --
# but the step must not read as a plain PASS either: the child reports its coverage
# in room.json and we surface it here.
$singleEndSteps = @()
if ($roomMetrics -and $roomMetrics.mode -eq 'single-end') {
    foreach ($s in $steps) {
        if ($s.name -like 'demo-room*' -and $s.ok) {
            $s | Add-Member -Force -NotePropertyName singleEnd -NotePropertyValue $true
        }
    }
    $singleEndSteps = @($steps | Where-Object { $_.singleEnd })
}

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
$degradedSteps = @($steps | Where-Object { $_.degraded })
$report['degraded'] = ($degradedSteps.Count -gt 0)
$report['degradedSteps'] = @($degradedSteps | ForEach-Object { $_.name })
$report['singleEnd'] = ($singleEndSteps.Count -gt 0)
$report['singleEndSteps'] = @($singleEndSteps | ForEach-Object { $_.name })
# A degraded step means "assertions held, coverage did not". That is not green.
# Single-end is the documented default coverage, so it stays a pass -- but flagged.
$report['passed'] = (($failed.Count -eq 0) -and (-not $report['degraded']))
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
        $status = if ($_.degraded) { 'DEGRADED' } elseif ($_.singleEnd) { 'SINGLE-END' } elseif ($_.ok) { 'PASS' } else { 'FAIL' }
        "| {0} | {1} | {2} s | {3} |" -f $_.name, $status, $_.seconds, $_.error
    }) -join "`n"

$overall = if ($failed.Count -gt 0) { 'FAIL' } elseif ($report.degraded) { 'DEGRADED' } elseif ($report.singleEnd) { 'PASS (single-end)' } else { 'PASS' }

$markdown = @(
    '# AI003 演示报告'
    ''
    ('生成时间：' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
    ('总体：' + $overall)
    $(if ($report.degraded) { '**DEGRADED: 未经双端验证** — 步骤 ' + ($report.degradedSteps -join '、') + ' 只验证了单端。' } else { $null })
    $(if ($report.singleEnd) { '**SINGLE-END: 未经双端验证** — 步骤 ' + ($report.singleEndSteps -join '、') + ' 按单端口径运行（默认），要双端结论请加 `-WithUE`。' } else { $null })
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
    $status = if ($step.degraded) { 'DEGRADED' } elseif ($step.singleEnd) { 'SINGLE-END' } elseif ($step.ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0,-16} {1,-11} {2} s" -f $step.name, $status, $step.seconds)
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
    [void](Stop-PortListener -Port $Port -ExpectedPid $gatewayPid)
}

if ($failed.Count -gt 0) {
    Write-Host ("demo hub failed: " + ($failed -join '; ')) -ForegroundColor Red
    exit 1
}
if ($degradedSteps.Count -gt 0) {
    Write-Host ''
    Write-Host 'DEGRADED: 未经双端验证' -ForegroundColor Yellow
    Write-Host ("  降级步骤: " + (($degradedSteps | ForEach-Object { $_.name }) -join ', ')) -ForegroundColor Yellow
    Write-Host '  断言通过但覆盖不足，不是全绿；退出码 2。' -ForegroundColor Yellow
    exit 2
}
if ($singleEndSteps.Count -gt 0) {
    Write-Host ''
    Write-Host 'SINGLE-END: 未经双端验证' -ForegroundColor Cyan
    Write-Host ("  单端步骤: " + (($singleEndSteps | ForEach-Object { $_.name }) -join ', ')) -ForegroundColor Cyan
    Write-Host '  这是默认口径（Unity 单端），断言已通过；要双端结论请加 -WithUE。' -ForegroundColor Cyan
}
Write-Host 'demo hub passed' -ForegroundColor Green
