#!/usr/bin/env pwsh
# Measures how much vegetation actually reached the frame, without guessing from
# colour: each frame is rendered twice, once normally and once with
# AI003_STILL_UNLIT=1 (AshkarStill swaps ONLY the vegetation material for a flat
# unlit colour), and the two renders are differenced. Pixels that moved are
# vegetation. See stills-vegdiff.py for the arithmetic.
#
# RENDER MODE CAVEAT (2026-11-14): this runs the editor with -batchmode, and in
# batchmode URP/Lit vegetation loses its texture-sampling warmth - tree pixels come
# out about 1.5 stops darker than a GUI render of the same rig (measured: luma 7.24
# vs 41.65 inside the vegetation mask, terrain/sky/fog identical to 0.05). So:
#   * APPEARANCE (colour, "are the trees black") must be judged on the GUI
#     artifacts\ashkar\*.png frames only - never on a file this script rendered.
#   * GEOMETRY (coverage %, crown count) is unaffected: the mask comes from the
#     same two batchmode renders, and darkening only widens the delta.
# AshkarStill logs mode=batchmode|gui in the shot= line so a frame can be traced.
#
#   scripts\stills-vegmask.cmd                       # forest + steppe
#   scripts\stills-vegmask.cmd -Frames forest
#
# Writes artifacts\stills\vegmask\<frame>.png, <frame>-unlit.png, <frame>-mask.png
# and artifacts\logs\vegmask-<frame>[-unlit].log. Exits non-zero when a frame has
# less vegetation than -MinBlobs.
#
# Needs full access: Unity's Package Manager child dies under the sandbox (named
# pipes blocked) and the editor then exits 1 before rendering.
[CmdletBinding()]
param(
    [string]$Unity = 'C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe',
    [string]$Project = '',
    [string[]]$Frames = @('forest', 'steppe'),
    [int]$TimeoutMinutes = 15,
    [int]$MinBlobs = 1,
    # Reuse the pngs already in artifacts\stills\vegmask instead of re-rendering.
    [switch]$SkipRender,
    # Wait this long for another editor to release Temp\UnityLockfile. Several bots
    # share one Unity project, so a lock collision is routine, not a broken run.
    [int]$LockWaitSeconds = 300
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Project) { $Project = Join-Path $root 'clients\unity\UnityGameClient' }

# "forest,steppe" arrives from cmd as ONE string; split it so -Frames takes both
# comma forms people actually type.
$Frames = @($Frames | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Frames.Count -eq 0) { throw '-Frames needs at least one frame name' }
$logDir = Join-Path $root 'artifacts\logs'
$maskDir = Join-Path $root 'artifacts\stills\vegmask'
$outDir = Join-Path $root 'artifacts\ashkar'
$python = 'C:\Users\xuyuehui\AppData\Local\Programs\DS Bot\resources\primary-runtime\dependencies\python\python.exe'

New-Item -ItemType Directory -Force -Path $logDir, $maskDir, $outDir | Out-Null
if (-not (Test-Path $Unity)) { throw "Unity editor not found: $Unity" }

function Wait-ProjectLock([int]$Seconds) {
    $lock = Join-Path $Project 'Temp\UnityLockfile'
    $deadline = (Get-Date).AddSeconds($Seconds)
    $announced = $false
    while ($true) {
        if (-not (Test-Path $lock)) { return $true }
        $held = $false
        try {
            $fs = [System.IO.File]::Open($lock, 'Open', 'ReadWrite', 'None')
            $fs.Dispose()
        } catch {
            $held = $true
        }
        if (-not $held) { return $true }
        if ((Get-Date) -ge $deadline) { return $false }
        if (-not $announced) {
            Write-Host ("==> waiting for another editor to release the project lock (up to {0}s)" -f $Seconds) -ForegroundColor Yellow
            $announced = $true
        }
        Start-Sleep -Seconds 5
    }
}

function Invoke-Still {
    param([string]$Frame, [switch]$Unlit)

    $tag = if ($Unlit) { "$Frame-unlit" } else { $Frame }
    $log = Join-Path $logDir "vegmask-$tag.log"
    Remove-Item $log -Force -ErrorAction SilentlyContinue

    if (-not (Wait-ProjectLock $LockWaitSeconds)) {
        throw ("project lock still held after {0}s - another Unity editor has {1} open; retry when it is done (see Temp\UnityLockfile)" -f $LockWaitSeconds, $Project)
    }

    $env:AI003_STILL_ONLY = $Frame
    if ($Unlit) { $env:AI003_STILL_UNLIT = '1' } else { Remove-Item -Path 'Env:AI003_STILL_UNLIT' -ErrorAction SilentlyContinue }

    Write-Host "==> rendering $tag" -ForegroundColor Cyan
    $process = Start-Process -FilePath $Unity -PassThru -ArgumentList @(
        '-batchmode',
        '-projectPath', $Project,
        '-executeMethod', 'AI003.EditorTools.AshkarStill.Run',
        '-quit',
        '-logFile', $log
    )
    if (-not $process.WaitForExit($TimeoutMinutes * 60000)) {
        try { $process.Kill() } catch { }
        throw "render of $tag timed out after $TimeoutMinutes min; see $log"
    }
    $process.WaitForExit()

    $png = Join-Path $outDir ($Frame + '.png')
    if (-not (Test-Path $png)) { throw "render of $tag produced no $png; see $log" }
    if (-not (Select-String -Path $log -Pattern ('\[AI003\]\[ASHKAR\] PASS .*' + $Frame + '\.png') -ErrorAction SilentlyContinue)) {
        throw "render of $tag logged no PASS; see $log"
    }

    $copy = Join-Path $maskDir ($tag + '.png')
    Copy-Item $png $copy -Force
    Write-Host "    $copy" -ForegroundColor DarkGray
    return $copy
}

# The unlit pass overwrites artifacts\ashkar\<frame>.png with the diagnostic
# render, which would silently replace the deliverable everyone else reviews.
# Always put the real frame back (also on failure): prefer the copy taken from the
# live frame BEFORE any render (the peer's artifact), else the normal pass.
function Restore-NormalFrame([string]$Frame) {
    $delivered = Join-Path $maskDir ($Frame + '-delivered.png')
    $normalCopy = Join-Path $maskDir ($Frame + '.png')
    $live = Join-Path $outDir ($Frame + '.png')
    $source = if (Test-Path $delivered) { $delivered } else { $normalCopy }
    if (-not (Test-Path $source)) { return }
    Copy-Item $source $live -Force
    Write-Host ("    restored real frame -> $live (from $(Split-Path $source -Leaf))") -ForegroundColor DarkGray
}

$rows = @()
try {
    foreach ($frame in $Frames) {
        if (-not $SkipRender) {
            # keep the peer's artifact exactly as it was before we touch the editor
            $liveFrame = Join-Path $outDir ($frame + '.png')
            if (Test-Path $liveFrame) {
                Copy-Item $liveFrame (Join-Path $maskDir ($frame + '-delivered.png')) -Force
            }
            $normal = Invoke-Still -Frame $frame
            try {
                $unlit = Invoke-Still -Frame $frame -Unlit
            } catch {
                # the unlit pass may have already overwritten the live frame
                Restore-NormalFrame $frame
                throw
            }
        } else {
            $normal = Join-Path $maskDir ($frame + '.png')
            $unlit = Join-Path $maskDir ($frame + '-unlit.png')
            if (-not (Test-Path $normal) -or -not (Test-Path $unlit)) {
                throw "-SkipRender needs $normal and $unlit"
            }
        }

        $mask = Join-Path $maskDir ($frame + '-mask.png')
        Write-Host ''
        try {
            $output = @(& $python (Join-Path $PSScriptRoot 'stills-vegdiff.py') $normal $unlit --mask $mask)
            if ($LASTEXITCODE -ne 0) { throw "vegdiff failed for $frame" }
        } finally {
            if (-not $SkipRender) { Restore-NormalFrame $frame }
        }
        $output | Write-Host

        $blobCount = 0
        $coverage = '-'
        foreach ($line in $output) {
            if ($line -match '^blobs\s*:\s*(\d+)') { $blobCount = [int]$Matches[1] }
            if ($line -match '^coverage:\s*([0-9.]+)%') { $coverage = $Matches[1] + '%' }
        }
        $rows += [pscustomobject]@{
            Frame    = $frame
            Blobs    = $blobCount
            Coverage = $coverage
            Verdict  = if ($blobCount -ge $MinBlobs) { 'PASS' } else { 'FAIL' }
        }
    }
} finally {
    Remove-Item -Path 'Env:AI003_STILL_ONLY' -ErrorAction SilentlyContinue
    Remove-Item -Path 'Env:AI003_STILL_UNLIT' -ErrorAction SilentlyContinue
}

Write-Host ''
$rows | Format-Table Frame, Blobs, Coverage, Verdict -AutoSize | Out-String -Width 4096 | Write-Host
$failed = @($rows | Where-Object { $_.Verdict -ne 'PASS' })
Write-Host ("==> {0}/{1} frames have vegetation (min blobs {2})" -f
    ($rows.Count - $failed.Count), $rows.Count, $MinBlobs) `
    -ForegroundColor $(if ($failed.Count -eq 0) { 'Green' } else { 'Red' })
Write-Host "masks+renders: $maskDir"
Write-Host "logs:         $logDir\vegmask-*.log"

if ($failed.Count -gt 0) { exit 1 }
exit 0
