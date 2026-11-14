#!/usr/bin/env pwsh
# One command that renders the six biome stills and proves they came out.
#
#   scripts\render-stills.cmd              # one run (GUI session, see -Mode)
#   scripts\render-stills.cmd -Repeat      # two runs, must be byte-identical
#
# Runs AI003.EditorTools.AshkarStill.Run, folds the editor log into
# artifacts/logs/stills.log, then fails unless all six frames are really there: one
# PASS line, one "shot=<name> ... origin=table ... cam=" line and a 1920x804 png per
# biome, each rewritten by THIS run.
#
# -Mode gui (default, decided 2026-11-14): launch the editor WITHOUT -batchmode, so
# UPM and shader variants load through the GUI path. Reason: in batchmode the
# vegetation's URP/Lit _NORMALMAP variant failed to compile and the trees rendered
# magenta (11.5% of the forest frame), and warm-up plus keyword stripping both
# failed to fix it - batchmode cannot produce an acceptable still. GUI renders are
# still byte-deterministic (same rig, same hash), so -Repeat keeps its meaning.
# Appearance is judged on GUI frames only; batchmode stays for geometry (the UNLIT
# difference used by stills-vegmask.ps1 is unaffected, it paints flat colours).
#
# -Repeat renders a second time and requires identical SHA256 per png, which is
# the "same command twice, zero pixel delta" half of the stage-1 acceptance
# check (no image library needed: identical bytes implies identical pixels).
#
# Either mode is deliberately NOT -nographics: the tool renders through a
# RenderTexture, and a null graphics device writes black frames.
#
# The six PNGs are deleted before the first run so a stale file from an earlier
# pass can never satisfy the check. Diagnostic frames (forest-unlit.png etc.) and
# the contact sheet are left alone.
#
# NOTE: this machine has no PowerShell 7. Use render-stills.cmd, or
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\render-stills.ps1
[CmdletBinding()]
param(
    [string]$Unity = 'C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe',
    [string]$Project = '',
    [int]$TimeoutMinutes = 25,
    [ValidateSet('gui', 'batchmode')]
    [string]$Mode = 'gui',
    # Wait this long for another editor to release Temp\UnityLockfile.
    [int]$LockWaitSeconds = 300,
    # Keep whatever pngs are already in artifacts/ashkar.
    [switch]$NoClean,
    # Do not strip AI003_STILL_* from the environment (diagnostic runs only).
    [switch]$AllowEnvOverride,
    # Render twice and require identical bytes.
    [switch]$Repeat,
    # Skip the world-data hash guard (only for diagnosing the guard itself).
    [switch]$SkipWorldHash,
    # After a passing run, diff the six frames against this directory and print
    # the per-frame drift (expected after a look change; missing frames still fail).
    [string]$Baseline = '',
    # Where the renders land. Experiments use a run directory so a bad frame can
    # never overwrite the accepted stills (that already happened three times).
    [string]$OutDir = '',
    # With -OutDir: copy the six frames into artifacts/ashkar after every check passes.
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Project) { $Project = Join-Path $root 'clients\unity\UnityGameClient' }

$logDir = Join-Path $root 'artifacts\logs'
$stillsDir = Join-Path $root 'artifacts\stills'
$log = Join-Path $logDir 'stills.log'
$report = Join-Path $stillsDir 'render-stills-report.txt'

# The acceptance set, in the order the Frames table renders them.
$frames = @('desert', 'ocean', 'forest', 'steppe', 'snow', 'mountain')
$expectedWidth = 1920
$expectedHeight = 804

foreach ($dir in @($logDir, $stillsDir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

if (-not (Test-Path $Unity)) { throw "Unity editor not found: $Unity" }
if (-not (Test-Path $Project)) { throw "Unity project not found: $Project" }

$running = @(Get-Process -Name Unity -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Warning ("Unity is already running (PID {0}); another editor run on the same project can fail to lock it." -f
        (($running | ForEach-Object { $_.Id }) -join ','))
}

# ---------------------------------------------------------------- environment ---
# The Frames table is the only source of truth; a leftover override would make
# the logged origin= read "env" and invalidate the comparison. AI003_STILL_OUTDIR is
# ours: it is set below, after the strip.
$stripped = @()
if (-not $AllowEnvOverride) {
    foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -like 'AI003_STILL*' })) {
        if ($item.Name -eq 'AI003_STILL_OUTDIR') { continue }
        $stripped += $item.Name
        Remove-Item -Path ('Env:' + $item.Name) -ErrorAction SilentlyContinue
    }
}
if ($stripped.Count -gt 0) {
    Write-Host ("==> stripped env overrides: " + ($stripped -join ', ')) -ForegroundColor Yellow
}

# ------------------------------------------------------------------ output dir ---
# artifacts/ashkar holds the accepted stills; a run with -OutDir renders elsewhere
# and only -Publish copies in, after every check passed.
$acceptedDir = Join-Path $root 'artifacts\ashkar'
if ($OutDir) {
    $outDir = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
} else {
    $outDir = $acceptedDir
}
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$env:AI003_STILL_OUTDIR = $outDir
$publishing = $Publish -and ($outDir -ne $acceptedDir)
if ($Publish -and -not $publishing) {
    Write-Warning '-Publish has no effect when rendering straight into artifacts\ashkar'
}
Write-Host "==> outDir: $outDir$(if ($publishing) { '  (will publish to artifacts\ashkar on PASS)' })"

# ------------------------------------------------------------- world data guard ---
# Hard rule for this goal: the world data is never re-signed. Every render re-checks
# artifacts\stills\world-hash.txt first, so a frame can never silently come from a
# rebuilt world. Skip with -SkipWorldHash only when diagnosing the guard itself.
$worldHashFile = Join-Path $stillsDir 'world-hash.txt'
$worldChecked = 0
if (-not $SkipWorldHash -and (Test-Path $worldHashFile)) {
    $worldProblems = @()
    foreach ($entry in @(Get-Content $worldHashFile)) {
        $m = [regex]::Match($entry, '^([0-9a-f]{64})\s+(\d+)\s+(\S+)$')
        if (-not $m.Success) { continue }
        $want = $m.Groups[1].Value
        $wantLen = [int64]$m.Groups[2].Value
        $rel = $m.Groups[3].Value
        $path = Join-Path $root ($rel -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path)) { $worldProblems += "missing $rel"; continue }
        $got = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLower()
        $len = (Get-Item -LiteralPath $path).Length
        $worldChecked++
        if ($got -ne $want -or $len -ne $wantLen) { $worldProblems += "hash changed: $rel" }
    }
    Write-Host ("==> world data: {0} files checked, {1} mismatch" -f $worldChecked, $worldProblems.Count) `
        -ForegroundColor $(if ($worldProblems.Count -eq 0) { 'Green' } else { 'Red' })
    if ($worldProblems.Count -gt 0) {
        foreach ($p in $worldProblems) { Write-Host ('    ' + $p) -ForegroundColor Red }
        throw 'world data changed - this goal forbids re-signing it; diff artifacts\stills\world-hash.txt before rendering again'
    }
} elseif (-not $SkipWorldHash) {
    Write-Warning "no $worldHashFile - world data guard skipped"
}

# ---------------------------------------------------------------- png helpers ---
function Get-PngSize([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $buf = New-Object byte[] 24
        if ($stream.Read($buf, 0, 24) -lt 24) { return $null }
        if ($buf[0] -ne 0x89 -or $buf[1] -ne 0x50 -or $buf[2] -ne 0x4E -or $buf[3] -ne 0x47) { return $null }
        $w = ([int]$buf[16] -shl 24) -bor ([int]$buf[17] -shl 16) -bor ([int]$buf[18] -shl 8) -bor [int]$buf[19]
        $h = ([int]$buf[20] -shl 24) -bor ([int]$buf[21] -shl 16) -bor ([int]$buf[22] -shl 8) -bor [int]$buf[23]
        return [pscustomobject]@{ Width = $w; Height = $h }
    } finally { $stream.Dispose() }
}

function Get-PngHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '-' }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-Shot {
    param([string[]]$Lines, [string]$Name, [datetime]$RunStart)
    $png = Join-Path $outDir ($Name + '.png')
    $problems = New-Object System.Collections.Generic.List[string]

    $shotLine = $Lines | Where-Object { $_ -match ('\[AI003\]\[ASHKAR\] shot=' + $Name + '\s') } | Select-Object -Last 1
    $passLine = $Lines | Where-Object { $_ -match ('\[AI003\]\[ASHKAR\] PASS .*' + $Name + '\.png') } | Select-Object -Last 1

    if (-not $shotLine) { $problems.Add('no shot= log line') }
    else {
        if ($shotLine -notmatch 'origin=table') { $problems.Add('origin!=table (table was overridden)') }
        if ($shotLine -notmatch 'cam=\(') { $problems.Add('no cam= in log line') }
    }
    if (-not $passLine) { $problems.Add('no PASS line') }

    $size = $null
    $fresh = $false
    if (-not (Test-Path -LiteralPath $png)) { $problems.Add('png missing') }
    else {
        $size = Get-PngSize $png
        $fresh = (Get-Item -LiteralPath $png).LastWriteTime -ge $RunStart.AddSeconds(-2)
        if (-not $size) { $problems.Add('not a png') }
        elseif ($size.Width -ne $expectedWidth -or $size.Height -ne $expectedHeight) {
            $problems.Add("size $($size.Width)x$($size.Height) != ${expectedWidth}x${expectedHeight}")
        }
        if (-not $fresh) { $problems.Add('png not rewritten by this run (stale)') }
    }

    $errors = @($Lines | Where-Object { $_ -match ('\[AI003\]\[ASHKAR\] shot ' + $Name + ' failed') })
    if ($errors.Count -gt 0) { $problems.Add('editor logged shot failure') }

    [pscustomobject]@{
        Frame   = $Name
        Verdict = if ($problems.Count -eq 0) { 'PASS' } else { 'FAIL' }
        Size    = if ($size) { "$($size.Width)x$($size.Height)" } else { '-' }
        Fresh   = if ($fresh) { 'yes' } else { 'no' }
        Origin  = if ($shotLine -and $shotLine -match 'origin=(\w+)') { $Matches[1] } else { '-' }
        Cam     = if ($shotLine -and $shotLine -match 'cam=(\([^)]*\))') { $Matches[1] } else { '-' }
        Hash    = Get-PngHash $png
        Detail  = ($problems -join '; ')
    }
}

# ---------------------------------------------------------------- one render ---
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

function Invoke-Render([datetime]$RunStart) {
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    if (-not (Wait-ProjectLock $LockWaitSeconds)) {
        return [pscustomobject]@{
            Rows = @(); LogLines = @(); Elapsed = [TimeSpan]::Zero; ExitCode = -1; TimedOut = $false
            LogProblems = @("project lock still held after $LockWaitSeconds s - another editor has $Project open")
        }
    }

    $launchArgs = @()
    if ($Mode -eq 'batchmode') { $launchArgs += '-batchmode' }
    $launchArgs += @(
        '-projectPath', $Project,
        '-executeMethod', 'AI003.EditorTools.AshkarStill.Run',
        '-quit',
        '-logFile', $log
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Unity -PassThru -ArgumentList $launchArgs

    $timedOut = $false
    if (-not $process.WaitForExit($TimeoutMinutes * 60000)) {
        $timedOut = $true
        try { $process.Kill() } catch { }
        Start-Sleep -Seconds 2
    } else {
        $process.WaitForExit()
    }
    $sw.Stop()
    $exitCode = if ($process.HasExited) { $process.ExitCode } else { -1 }

    $elapsed = $sw.Elapsed

    if (-not (Test-Path $log)) {
        return [pscustomobject]@{
            Rows = @(); LogLines = @(); Elapsed = $elapsed; ExitCode = $exitCode; TimedOut = $timedOut
            LogProblems = @("Unity wrote no log to $log (exit=$exitCode, $([int]$elapsed.TotalSeconds)s)")
        }
    }

    $lines = @(Get-Content $log)

    $logProblems = New-Object System.Collections.Generic.List[string]
    if ($timedOut) { $logProblems.Add("timeout after $TimeoutMinutes min") }
    if ($exitCode -ne 0) { $logProblems.Add("editor exit code $exitCode") }
    if (-not ($lines | Where-Object { $_ -match '\[AI003\]\[ASHKAR\] PASS ' })) {
        $logProblems.Add('no [ASHKAR] PASS line at all - check for a compile error in the log')
    }
    foreach ($line in ($lines | Where-Object { $_ -match '\[AI003\]\[ASHKAR\] FAIL' })) {
        $logProblems.Add('editor FAIL: ' + $line)
    }
    if ($lines | Where-Object { ($_ -match 'error CS\d+') -and ($_ -match 'Assets') }) {
        $logProblems.Add('compile errors in log')
    }

    $rows = @(foreach ($name in $frames) { Assert-Shot -Lines $lines -Name $name -RunStart $RunStart })

    # ---------------------------------------------------------- contact sheet ---
    # Pure function of the six pngs plus their shot lines, so two runs that agree on
    # the frames must also agree on the sheet - which lets -Repeat cover it too.
    $sheet = Join-Path $outDir 'six-terrain-sheet.png'
    $labels = [ordered]@{}
    foreach ($name in $frames) {
        $line = $lines | Where-Object { $_ -match ('\[AI003\]\[ASHKAR\] shot=' + $name + '\s') } | Select-Object -Last 1
        if (-not $line) { continue }
        $cam = if ($line -match 'cam=(\([^)]*\))') { $Matches[1] } else { 'cam=?' }
        $fov = if ($line -match 'fov=([0-9.]+)') { "fov=$($Matches[1])" } else { '' }
        $labels[$name] = ("cam=$cam $fov origin=table").Trim()
    }
    $labelsPath = Join-Path $stillsDir 'sheet-labels.json'
    $labels | ConvertTo-Json | Set-Content -Path $labelsPath -Encoding UTF8
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        & python (Join-Path $PSScriptRoot 'make-stills-sheet.py') --dir $outDir --out $sheet --labels $labelsPath | Out-Null
        if ($LASTEXITCODE -ne 0) { $logProblems.Add("contact sheet generation failed (exit $LASTEXITCODE)") }
    }
    else {
        $logProblems.Add('contact sheet skipped: python not on PATH')
    }
    $sheetHash = if (Test-Path $sheet) { (Get-FileHash -LiteralPath $sheet -Algorithm SHA256).Hash } else { '-' }

    [pscustomobject]@{
        Rows = $rows; LogLines = $lines; Elapsed = $elapsed; ExitCode = $exitCode
        TimedOut = $timedOut; LogProblems = $logProblems.ToArray()
        SheetHash = $sheetHash; Sheet = $sheet
    }
}

# ------------------------------------------------------------------ clean slate ---
$removed = @()
$locked = @()
if (-not $NoClean) {
    foreach ($name in $frames) {
        $png = Join-Path $outDir ($name + '.png')
        if (-not (Test-Path $png)) { continue }
        # Another bot may be looking at these images; a transient share lock must
        # not abort the run. The freshness check still proves the png came out of
        # THIS run, so a locked leftover cannot fake a pass.
        $gone = $false
        for ($i = 0; $i -lt 3 -and -not $gone; $i++) {
            try {
                Remove-Item $png -Force -ErrorAction Stop
                $gone = $true
            } catch {
                Start-Sleep -Seconds 1
            }
        }
        if ($gone) { $removed += $name } else { $locked += $name }
    }
}
if ($removed.Count -gt 0) {
    Write-Host ("==> removed stale pngs: " + ($removed -join ', ')) -ForegroundColor DarkGray
}
if ($locked.Count -gt 0) {
    Write-Warning ("could not delete (open elsewhere): " + ($locked -join ', ') +
        " - the freshness check will still reject a stale file")
}

# ----------------------------------------------------------------------- render ---
$total = if ($Repeat) { 2 } else { 1 }
$runs = @()
for ($i = 1; $i -le $total; $i++) {
    Write-Host ("==> run {0}/{1}: rendering six biome stills ({2})" -f $i, $total, $Project) -ForegroundColor Cyan
    $runStart = Get-Date
    $result = Invoke-Render -RunStart $runStart
    $runs += $result
    $okCount = @($result.Rows | Where-Object { $_.Verdict -eq 'PASS' }).Count
    Write-Host ("    {0}/{1} PASS  exit={2}  {3}s" -f
        $okCount, $frames.Count, $result.ExitCode, [int]$result.Elapsed.TotalSeconds)
}

$rows = @($runs[0].Rows)
$allProblems = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $runs.Count; $i++) {
    foreach ($p in $runs[$i].LogProblems) { $allProblems.Add("run$($i + 1): $p") }
}

# ------------------------------------------------------------- repeat comparison ---
$repeatVerdict = '-'
if ($Repeat) {
    $repeatVerdict = 'identical'
    $second = $runs[1]
    foreach ($row in $rows) {
        $other = $second.Rows | Where-Object { $_.Frame -eq $row.Frame } | Select-Object -First 1
        if (-not $other -or $other.Hash -eq '-') {
            $repeatVerdict = 'DIFF'
            $row.Verdict = 'FAIL'
            $row.Detail = ($row.Detail + '; second run produced no png').Trim('; ')
        } elseif ($other.Hash -ne $row.Hash) {
            $repeatVerdict = 'DIFF'
            $row.Verdict = 'FAIL'
            $row.Detail = ($row.Detail + "; pixel drift between runs ($($row.Hash.Substring(0,10)) vs $($other.Hash.Substring(0,10)))").Trim('; ')
        }
    }
    foreach ($row in $rows) {
        $other = $second.Rows | Where-Object { $_.Frame -eq $row.Frame } | Select-Object -First 1
        $row | Add-Member -NotePropertyName Repeat -NotePropertyValue $(if ($other -and $other.Hash -eq $row.Hash) { 'identical' } else { 'DIFF' })
    }
    if ($repeatVerdict -ne 'identical') {
        $allProblems.Add('two consecutive runs differ - the command is not reproducible yet')
    }
    # the sheet must be reproducible too
    if ($runs[0].SheetHash -ne $runs[1].SheetHash) {
        $repeatVerdict = 'DIFF'
        $allProblems.Add("contact sheet differs between runs ($($runs[0].SheetHash.Substring(0,10)) vs $($runs[1].SheetHash.Substring(0,10)))")
    }
}

$failures = @($rows | Where-Object { $_.Verdict -ne 'PASS' })

# ------------------------------------------------------------- baseline drift ---
# Only meaningful after a passing run: numbers, not a gate. A look change is
# supposed to move pixels; a missing/short frame is still an error.
$baselineLines = @()
if ($Baseline) {
    if (-not (Test-Path $Baseline)) {
        $allProblems.Add("baseline not found: $Baseline")
    }
    elseif (-not (Get-Command python -ErrorAction SilentlyContinue)) {
        $allProblems.Add('baseline diff needs python on PATH')
    }
    else {
        $diffOut = @(& python (Join-Path $PSScriptRoot 'stills-diff.py') $Baseline $outDir 2>&1)
        $baselineLines = $diffOut
        if ($LASTEXITCODE -ne 0) {
            $allProblems.Add("baseline diff failed (exit $LASTEXITCODE); see output above")
        }
    }
}

# ------------------------------------------------------------------------ report ---
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("render-stills report  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine("unity=$Unity")
[void]$sb.AppendLine("project=$Project")
[void]$sb.AppendLine("log=$log")
[void]$sb.AppendLine("mode=$Mode (gui = appearance source; batchmode = geometry only)")
[void]$sb.AppendLine(("world data: {0} files checked, 0 mismatch" -f $worldChecked))
for ($i = 0; $i -lt $runs.Count; $i++) {
    [void]$sb.AppendLine(("run{0}: exit={1} elapsed={2}s" -f ($i + 1), $runs[$i].ExitCode, [int]$runs[$i].Elapsed.TotalSeconds))
}
if ($Repeat) { [void]$sb.AppendLine("repeat=$repeatVerdict (sha256 per png)") }
if ($stripped.Count -gt 0) { [void]$sb.AppendLine("stripped env=$($stripped -join ',')") }
if ($removed.Count -gt 0) { [void]$sb.AppendLine("removed stale=$($removed -join ',')") }
[void]$sb.AppendLine('')
[void]$sb.AppendLine(($rows | Format-Table Frame, Verdict, Size, Fresh, Origin, Cam -AutoSize | Out-String -Width 4096).TrimEnd())
[void]$sb.AppendLine('sha256:')
foreach ($row in $rows) { [void]$sb.AppendLine("  $($row.Frame.PadRight(9)) $($row.Hash)") }
if ($allProblems.Count -gt 0) {
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('problems:')
    foreach ($p in $allProblems) { [void]$sb.AppendLine("  - $p") }
}
[void]$sb.AppendLine('')
[void]$sb.AppendLine('shot lines:')
foreach ($line in ($runs[0].LogLines | Where-Object { $_ -match '\[AI003\]\[ASHKAR\] shot=' })) {
    [void]$sb.AppendLine('  ' + $line)
}
if ($baselineLines.Count -gt 0) {
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("baseline drift vs $Baseline :")
    foreach ($line in $baselineLines) { [void]$sb.AppendLine('  ' + $line) }
}
Set-Content -Path $report -Value $sb.ToString() -Encoding UTF8

# ------------------------------------------------------------------------- output ---
Write-Host ''
$columns = @('Frame', 'Verdict', 'Size', 'Fresh', 'Origin', 'Cam')
if ($Repeat) { $columns += 'Repeat' }
$rows | Format-Table $columns -AutoSize | Out-String -Width 4096 | Write-Host

foreach ($row in $rows) {
    if ($row.Verdict -ne 'PASS') { Write-Host ("FAIL {0}: {1}" -f $row.Frame, $row.Detail) -ForegroundColor Red }
}

$ok = ($failures.Count -eq 0) -and ($allProblems.Count -eq 0)
Write-Host ("==> {0}/{1} frames PASS  runs={2}  repeat={3}  total={4}s" -f
    ($rows.Count - $failures.Count), $rows.Count, $runs.Count, $repeatVerdict,
    [int](($runs | ForEach-Object { $_.Elapsed.TotalSeconds } | Measure-Object -Sum).Sum)) `
    -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
Write-Host "==> log:    $log"
Write-Host "==> report: $report"
Write-Host "==> outDir: $outDir"

if (-not $ok) {
    foreach ($p in $allProblems) { Write-Host ("PROBLEM: " + $p) -ForegroundColor Red }
    exit 1
}

# Publish last: only a fully passing run may touch the accepted stills.
if ($publishing) {
    Write-Host '==> publishing to artifacts\ashkar' -ForegroundColor Cyan
    foreach ($name in $frames) {
        Copy-Item (Join-Path $outDir ($name + '.png')) (Join-Path $acceptedDir ($name + '.png')) -Force
    }
    $sheetSrc = Join-Path $outDir 'six-terrain-sheet.png'
    if (Test-Path $sheetSrc) {
        Copy-Item $sheetSrc (Join-Path $acceptedDir 'six-terrain-sheet.png') -Force
    }
    Add-Content -Path $report -Value ("published 6 frames + contact sheet to $acceptedDir at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    Write-Host "==> published 6 frames + contact sheet to $acceptedDir" -ForegroundColor Green
}
exit 0
