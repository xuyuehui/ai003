param(
    [string]$Engine = 'C:\Program Files\Epic Games\UE_5.8'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'clients\ue5\UE5GameClient\UE5GameClient.uproject'
$editor = Join-Path $Engine 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$log = Join-Path $repo 'clients\ue5\UE5GameClient\Saved\Logs\UE5GameClient.log'
$stdout = Join-Path $repo 'artifacts\logs\ue-input-probe.log'

Write-Host '==> UE input probe (camera-relative mapping)' -ForegroundColor Cyan
$process = Start-Process -FilePath $editor -PassThru -NoNewWindow `
    -RedirectStandardOutput $stdout -RedirectStandardError "$stdout.err" `
    -ArgumentList @($project, '-game', '-nullrhi', '-nosplash', '-AI003InputProbe', '-log')
if (-not $process.WaitForExit(300000)) { $process.Kill(); throw 'UE input probe timed out' }

$lines = Select-String -Path $log -Pattern '\[AI003\]\[INPUTPROBE\]' | ForEach-Object { $_.Line }
if (-not $lines) { throw "no INPUTPROBE lines in $log" }
$lines | ForEach-Object { Write-Host $_ }

$expected = @(
    'yaw=0 W=(1,0) A=(0,1) D=(0,-1) S=(-1,0)',
    'yaw=90 W=(0,-1)',
    'yaw=180 W=(-1,0)',
    'yaw=270 W=(0,1)'
)

$joined = $lines -join "`n"
$missing = @()
foreach ($pattern in $expected) {
    if ($joined -notlike "*$pattern*") { $missing += $pattern }
}

if ($missing.Count -gt 0) {
    foreach ($pattern in $missing) { Write-Host "FAIL missing: $pattern" -ForegroundColor Red }
    exit 1
}

Write-Host 'UE input mapping verified' -ForegroundColor Green
