param(
    [string]$Unity = 'C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe',
    [string]$Engine = 'C:\Program Files\Epic Games\UE_5.8',
    [int]$Seconds = 5,
    [switch]$KeepRunning,
    [switch]$NoPublish,
    [switch]$NoAssert,
    [ValidateSet('frame', 'arena')][string]$Sync = 'frame'
)

$ErrorActionPreference = 'Stop'
$roomName = 'demo-' + (Get-Date -Format 'MMddHHmmss')
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class DemoRoomFocus {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@

$repo = Split-Path $PSScriptRoot -Parent
$demoDir = Join-Path $repo 'artifacts\demo'
$logs = Join-Path $repo 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $demoDir, $logs | Out-Null

$unityShot = Join-Path $demoDir 'room-unity.png'
$ueShot = Join-Path $demoDir 'room-ue5.png'
$compare = Join-Path $demoDir 'room-compare.png'
$baseUrl = 'http://127.0.0.1:8080'

function Test-Gateway {
    try { Invoke-RestMethod "$baseUrl/healthz" -TimeoutSec 2 | Out-Null; return $true } catch { return $false }
}

function Get-Demo { Invoke-RestMethod "$baseUrl/demo" -TimeoutSec 5 }

function Focus-UE {
    $game = Get-Process | Where-Object { $_.MainWindowTitle -like '*UE5GameClient*' } | Select-Object -First 1
    if (-not $game) { return $false }
    [DemoRoomFocus]::ShowWindow($game.MainWindowHandle, 9) | Out-Null
    [DemoRoomFocus]::SetForegroundWindow($game.MainWindowHandle) | Out-Null
    return $true
}


# --- server -----------------------------------------------------------------
$serverProcess = $null
if (-not (Test-Gateway)) {
    $serverExe = Join-Path $repo 'server\bin\ai003-server.exe'
    if (-not (Test-Path $serverExe)) {
        & (Join-Path $PSScriptRoot 'build-server.ps1')
    }
    Write-Host '==> starting gateway' -ForegroundColor Cyan
    $serverProcess = Start-Process -FilePath $serverExe -ArgumentList '-addr', '127.0.0.1:8080' -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 60 -and -not (Test-Gateway); $i++) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Gateway)) { throw 'gateway did not become healthy' }
}
else {
    Write-Host '==> gateway already running' -ForegroundColor Cyan
}

$demoBefore = Get-Demo

$unityProcess = $null
$ueProcess = $null
try {
    # --- Unity -------------------------------------------------------------
    Write-Host '==> Unity: enter room and capture' -ForegroundColor Cyan
    Remove-Item $unityShot -Force -ErrorAction SilentlyContinue
    $unityProject = Join-Path $repo 'clients\unity\UnityGameClient'
    $previousRoom = $env:AI003_ROOM
    $previousRoomName = $env:AI003_ROOM_NAME
    $previousHold = $env:AI003_ROOM_HOLD
    $previousArena = $env:AI003_ARENA
    $env:AI003_ROOM = '1'
    $env:AI003_ROOM_NAME = $roomName
    $env:AI003_ROOM_HOLD = '30'
    if ($Sync -eq 'arena') { $env:AI003_ROOM = '0'; $env:AI003_ARENA = '1' }
    else { Remove-Item Env:AI003_ARENA -ErrorAction SilentlyContinue }
    try {
        $unityProcess = Start-Process -FilePath $Unity -PassThru `
            -ArgumentList @('-projectPath', $unityProject,
                '-executeMethod', 'AI003.EditorTools.CaptureRoom.Run',
                '-logFile', (Join-Path $logs 'unity-room-shot.log')) `
            -RedirectStandardOutput (Join-Path $logs 'unity-room.cpp.out') `
            -RedirectStandardError (Join-Path $logs 'unity-room.cpp.err')
    }
    finally {
        $env:AI003_ROOM = $previousRoom
        $env:AI003_ROOM_NAME = $previousRoomName
        $env:AI003_ROOM_HOLD = $previousHold
        if ($null -eq $previousArena) { Remove-Item Env:AI003_ARENA -ErrorAction SilentlyContinue }
        else { $env:AI003_ARENA = $previousArena }
    }

    Start-Sleep -Seconds 5

    # --- UE ----------------------------------------------------------------
    Write-Host "==> UE5: join $roomName as a bot" -ForegroundColor Cyan
    Remove-Item $ueShot -Force -ErrorAction SilentlyContinue
    $ueProject = Join-Path $repo 'clients\ue5\UE5GameClient\UE5GameClient.uproject'
    $ueEditor = Join-Path $Engine 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
    $ueProcess = Start-Process -FilePath $ueEditor -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $logs 'ue-room-shot.log') `
        -RedirectStandardError (Join-Path $logs 'ue-room-shot.err') `
        -ArgumentList (@($ueProject, '-game', '-windowed', '-ResX=1280', '-ResY=720',
            '-nosplash', '-AlwaysOnTop', '-AI003Room', '-AI003RoomBot', '-AI003RoomShot',
            "-AI003RoomName=$roomName", '-log') + $(if ($Sync -eq 'arena') { @('-AI003Arena') } else { @() }))
    Start-Sleep -Seconds 8
    [void](Focus-UE)

    # --- wait for two fighters ---------------------------------------------
    Write-Host '==> waiting for two fighters in one room' -ForegroundColor Cyan
    $deadline = (Get-Date).AddSeconds(150)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $demo = Get-Demo
            $room = @($demo.rooms) | Where-Object { $_.id -eq $roomName -and $_.players -ge 2 } | Select-Object -First 1
            if ($room) { $ready = $true; break }
        }
        catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'timed out waiting for two fighters (Unity/UE did not join)' }
    Write-Host ("==> room {0} has {1} fighters, letting the HUD settle" -f $room.id, $room.players) -ForegroundColor Cyan
    Start-Sleep -Seconds $Seconds

    # Wait until the bot actually engages, otherwise short runs capture an idle
    # frame and the attack/hit assertions flap.
    $combatDeadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $combatDeadline) {
        try {
            $probe = Get-Demo
            if ([int64]$probe.stats.attackEvents -gt [int64]$demoBefore.stats.attackEvents) { break }
        }
        catch { }
        Start-Sleep -Milliseconds 500
    }

    # --- wait for the UE screenshot (rendered by the game itself) ----------
    Write-Host '==> waiting for UE room screenshot' -ForegroundColor Cyan
    $ueDeadline = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $ueDeadline -and $ueProcess -and -not $ueProcess.HasExited) {
        Start-Sleep -Seconds 1
    }
    if ($ueProcess -and -not $ueProcess.HasExited) { $ueProcess.Kill() }
    if (-not (Test-Path $ueShot)) { throw "UE room shot missing: $ueShot (see artifacts/logs/ue-room-shot.log)" }
    # --- wait for Unity to finish ------------------------------------------
    if ($unityProcess -and -not $unityProcess.WaitForExit(150000)) { $unityProcess.Kill() }
    if (-not (Test-Path $unityShot)) { throw "Unity room shot missing: $unityShot (see artifacts/logs/unity-room-shot.log)" }

    # --- compose -----------------------------------------------------------
    Write-Host '==> composing room comparison' -ForegroundColor Cyan
    $left = [System.Drawing.Image]::FromFile($unityShot)
    $right = [System.Drawing.Image]::FromFile($ueShot)
    try {
        $labelHeight = 36
        $canvasWidth = $left.Width + $right.Width
        $canvasHeight = [Math]::Max($left.Height, $right.Height) + $labelHeight
        $canvas = New-Object System.Drawing.Bitmap $canvasWidth, $canvasHeight
        $g = [System.Drawing.Graphics]::FromImage($canvas)
        try {
            $g.Clear([System.Drawing.Color]::FromArgb(18, 20, 24))
            $g.DrawImage($left, 0, $labelHeight, $left.Width, $left.Height)
            $g.DrawImage($right, $left.Width, $labelHeight, $right.Width, $right.Height)
            $font = New-Object System.Drawing.Font('Segoe UI', 14, [System.Drawing.FontStyle]::Bold)
            $g.DrawString('Unity 6 - room', $font, [System.Drawing.Brushes]::White, 12, 8)
            $g.DrawString('UE 5.8 - same room', $font, [System.Drawing.Brushes]::White, ($left.Width + 12), 8)
            $g.DrawLine([System.Drawing.Pens]::DimGray, $left.Width, 0, $left.Width, $canvasHeight)
            $canvas.Save($compare, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $g.Dispose(); $canvas.Dispose() }
    }
    finally { $left.Dispose(); $right.Dispose() }

    Write-Host "room compare: $compare"
    if (-not $NoPublish) {
        $published = Join-Path $repo 'docs\images\demo-room.png'
        Copy-Item $compare $published -Force
        Write-Host "published: $published"
    }

    $demo = Get-Demo
    Write-Host ''
    Write-Host '--- server counters ---'
    & (Join-Path $PSScriptRoot 'demo-status.ps1') -BaseUrl $baseUrl

    # --- assertions --------------------------------------------------------
    $problems = @()
    $roomAfter = @($demo.rooms) | Where-Object { $_.id -eq $room.id } | Select-Object -First 1

    # Clients intentionally exit after their screenshots, so the empty-room
    # cleanup may already have removed the room by now; that is not a failure.
    if ($room.players -lt 2) { $problems += "room had $($room.players) fighters at capture time" }
    if ($roomAfter) {
        if ($roomAfter.tick -le $room.tick) { $problems += "room tick did not advance ($($room.tick) -> $($roomAfter.tick))" }
    }

    $frameDelta = [int64]$demo.stats.roomFrames - [int64]$demoBefore.stats.roomFrames
    $attackDelta = [int64]$demo.stats.attackEvents - [int64]$demoBefore.stats.attackEvents
    $hitDelta = [int64]$demo.stats.hitEvents - [int64]$demoBefore.stats.hitEvents
    if ($Sync -ne 'arena') {
        if ($frameDelta -le 0) { $problems += "no room frames observed" }
        if ($attackDelta -le 0) { $problems += "no attack events observed" }
        if ($hitDelta -le 0) { $problems += "no hit events observed" }
    }

    foreach ($shot in @($unityShot, $ueShot)) {
        if (-not (Test-Path $shot)) { $problems += "missing screenshot $shot"; continue }
        if ((Get-Item $shot).Length -lt 20000) { $problems += "screenshot too small: $shot" }
    }

    $playersNow = $room.players
    $tickDelta = if ($roomAfter) { $roomAfter.tick - $room.tick } else { $frameDelta }

    Write-Host ''
    Write-Host ("checks: roomPlayers={0} tickDelta={1} frameDelta={2} attackDelta={3} hitDelta={4}" -f `
        $playersNow, $tickDelta, $frameDelta, $attackDelta, $hitDelta)

    $metricsPath = Join-Path $repo 'artifacts\demo\room.json'
    [ordered]@{
        generatedAt = (Get-Date).ToString('o')
        roomId      = $room.id
        players     = [int]$playersNow
        tickDelta   = [int]$tickDelta
        frameDelta  = [int]$frameDelta
        attackDelta = [int]$attackDelta
        hitDelta    = [int]$hitDelta
        problems    = @($problems)
        passed      = ($problems.Count -eq 0)
    } | ConvertTo-Json -Depth 4 | Set-Content -Path $metricsPath -Encoding UTF8
    Write-Host "metrics: $metricsPath"

    if ($problems.Count -gt 0) {
        if ($NoAssert) {
            foreach ($problem in $problems) { Write-Host "WARN: $problem" -ForegroundColor Yellow }
        }
        else {
            foreach ($problem in $problems) { Write-Host "FAIL: $problem" -ForegroundColor Red }
            throw 'demo-room assertions failed'
        }
    }
    elseif (-not $NoAssert) {
        Write-Host 'room demo assertions passed' -ForegroundColor Green
    }
}
finally {
    if (-not $KeepRunning) {
        if ($ueProcess -and -not $ueProcess.HasExited) { $ueProcess.Kill(); $ueProcess.WaitForExit(5000) | Out-Null }
        if ($unityProcess -and -not $unityProcess.HasExited) { $unityProcess.Kill(); $unityProcess.WaitForExit(5000) | Out-Null }
        if ($serverProcess -and -not $serverProcess.HasExited) { $serverProcess.Kill(); $serverProcess.WaitForExit(5000) | Out-Null }
    }
}




