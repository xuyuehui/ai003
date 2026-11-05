param(
    [string]$Unity = 'C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe',
    [string]$Engine = 'C:\Program Files\Epic Games\UE_5.8',
    [switch]$SkipUnity,
    [switch]$SkipUE,
    [switch]$NoPublish,
    [double]$MaxBrightnessDelta = 999,
    [double]$MinWithin24 = 0
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$demoDir = Join-Path $repo 'artifacts\demo'
$logs = Join-Path $repo 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $demoDir, $logs | Out-Null

$unityShot = Join-Path $demoDir 'unity.png'
$ueShot = Join-Path $demoDir 'ue5.png'
$compare = Join-Path $demoDir 'compare.png'
$diffPath = Join-Path $demoDir 'diff.png'

if (-not $SkipUnity) {
    Write-Host '==> Unity demo frame (batch, fixed camera)' -ForegroundColor Cyan
    $project = Join-Path $repo 'clients\unity\UnityGameClient'
    $log = Join-Path $logs 'unity-demo-shot.log'
    Remove-Item $unityShot -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $Unity -PassThru -NoNewWindow `
        -RedirectStandardOutput "$log.out" -RedirectStandardError "$log.err" `
        -ArgumentList @('-batchmode', '-projectPath', $project, '-executeMethod',
            'AI003.EditorTools.CaptureDemoShot.Run', '-quit', '-logFile', $log)
    if (-not $process.WaitForExit(600000)) { $process.Kill(); throw 'Unity demo shot timed out' }
    if (-not (Test-Path $unityShot)) { throw "Unity demo shot missing: $unityShot (see $log)" }
}

if (-not $SkipUE) {
    Write-Host '==> UE5 demo frame (fixed camera)' -ForegroundColor Cyan
    $project = Join-Path $repo 'clients\ue5\UE5GameClient\UE5GameClient.uproject'
    $editor = Join-Path $Engine 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
    Remove-Item $ueShot -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $editor -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $logs 'ue-demoshot.log') `
        -RedirectStandardError (Join-Path $logs 'ue-demoshot.err') `
        -ArgumentList @($project, '-game', '-windowed', '-ResX=1280', '-ResY=720',
            '-nosplash', '-AI003DemoShot', '-log')
    if (-not $process.WaitForExit(600000)) { $process.Kill(); throw 'UE demo shot timed out' }
    if (-not (Test-Path $ueShot)) { throw "UE demo shot missing: $ueShot" }
}

Write-Host '==> composing side-by-side comparison' -ForegroundColor Cyan
Add-Type -AssemblyName System.Drawing

$left = [System.Drawing.Image]::FromFile($unityShot)
$right = [System.Drawing.Image]::FromFile($ueShot)
try {
    $labelHeight = 36
    $width = $left.Width + $right.Width
    $height = [Math]::Max($left.Height, $right.Height) + $labelHeight
    $canvas = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $graphics.Clear([System.Drawing.Color]::FromArgb(18, 20, 24))
        $graphics.DrawImage($left, 0, $labelHeight, $left.Width, $left.Height)
        $graphics.DrawImage($right, $left.Width, $labelHeight, $right.Width, $right.Height)

        $font = New-Object System.Drawing.Font('Segoe UI', 14, [System.Drawing.FontStyle]::Bold)
        $brush = [System.Drawing.Brushes]::White
        $graphics.DrawString('Unity 6 - URP', $font, $brush, 12, 8)
        $graphics.DrawString('Unreal Engine 5.8', $font, $brush, ($left.Width + 12), 8)
        $graphics.DrawLine([System.Drawing.Pens]::DimGray, $left.Width, 0, $left.Width, $height)

        $canvas.Save($compare, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $canvas.Dispose()
    }
}
finally {
    $left.Dispose()
    $right.Dispose()
}

Write-Host "compare: $compare"

function Get-MeanBrightness([string]$path) {
    $bitmap = [System.Drawing.Bitmap]::FromFile($path)
    try {
        $sum = 0.0
        $n = 0
        for ($y = 0; $y -lt $bitmap.Height; $y += 8) {
            for ($x = 0; $x -lt $bitmap.Width; $x += 8) {
                $color = $bitmap.GetPixel($x, $y)
                $sum += ($color.R + $color.G + $color.B) / 3.0
                $n++
            }
        }
        return [Math]::Round($sum / $n, 1)
    }
    finally { $bitmap.Dispose() }
}

function Read-Pixels([string]$path) {
    $bitmap = [System.Drawing.Bitmap]::FromFile($path)
    try {
        $rect = New-Object System.Drawing.Rectangle 0, 0, $bitmap.Width, $bitmap.Height
        $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $stride = $data.Stride
        try {
            $bytes = New-Object byte[] ($stride * $bitmap.Height)
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
        }
        finally { $bitmap.UnlockBits($data) }
        return [pscustomobject]@{ Bytes = $bytes; Stride = $stride; Width = $bitmap.Width; Height = $bitmap.Height }
    }
    finally { $bitmap.Dispose() }
}

if ((Test-Path $unityShot) -and (Test-Path $ueShot)) {
    $unityMean = Get-MeanBrightness $unityShot
    $ueMean = Get-MeanBrightness $ueShot
    Write-Host ("brightness: Unity {0} / UE {1} (delta {2})" -f $unityMean, $ueMean, [Math]::Round($unityMean - $ueMean, 1))

    # Full-resolution diff + heatmap (black = identical, red/yellow = large).
    $leftPixels = Read-Pixels $unityShot
    $rightPixels = Read-Pixels $ueShot
    $width = [Math]::Min($leftPixels.Width, $rightPixels.Width)
    $height = [Math]::Min($leftPixels.Height, $rightPixels.Height)
    $samples = $width * $height

    $heatmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $rect = New-Object System.Drawing.Rectangle 0, 0, $width, $height
    $target = $heatmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $heatStride = $target.Stride
    $heat = New-Object byte[] ($heatStride * $height)

    $diffSum = 0.0
    $maxDiff = 0.0
    $close = 0

    try {
        for ($y = 0; $y -lt $height; $y++) {
            $row = $y * $leftPixels.Stride
            $outRow = $y * $heatStride
            for ($x = 0; $x -lt $width; $x++) {
                $i = $row + $x * 3
                $db = [Math]::Abs([int]$leftPixels.Bytes[$i] - [int]$rightPixels.Bytes[$i])
                $dg = [Math]::Abs([int]$leftPixels.Bytes[$i + 1] - [int]$rightPixels.Bytes[$i + 1])
                $dr = [Math]::Abs([int]$leftPixels.Bytes[$i + 2] - [int]$rightPixels.Bytes[$i + 2])
                $diff = ($db + $dg + $dr) / 3.0

                $diffSum += $diff
                if ($diff -gt $maxDiff) { $maxDiff = $diff }
                if ($diff -le 24) { $close++ }

                if ($diff -lt 85) { $red = [int]($diff * 3); $green = 0 }
                elseif ($diff -lt 170) { $red = 255; $green = [int](($diff - 85) * 3) }
                else { $red = 255; $green = 255 }

                $out = $outRow + $x * 3
                $heat[$out] = 0
                $heat[$out + 1] = [byte]$green
                $heat[$out + 2] = [byte]$red
            }
        }

        [System.Runtime.InteropServices.Marshal]::Copy($heat, 0, $target.Scan0, $heat.Length)
    }
    finally {
        $heatmap.UnlockBits($target)
    }
    $heatmap.Save($diffPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $heatmap.Dispose()

    Write-Host ("pixel diff: mean {0:N1}/255  max {1:N0}  within 24: {2:N1}%  ({3} px)" -f `
        ($diffSum / $samples), $maxDiff, (100.0 * $close / $samples), $samples)
    Write-Host "diff heatmap: $diffPath"

    $within24 = 100.0 * $close / $samples
    $brightnessDelta = [Math]::Abs($unityMean - $ueMean)
    $failed = @()
    if ($brightnessDelta -gt $MaxBrightnessDelta) {
        $failed += ("brightness delta {0:N1} > {1:N1}" -f $brightnessDelta, $MaxBrightnessDelta)
    }
    if ($within24 -lt $MinWithin24) {
        $failed += ("within-24 {0:N1}% < {1:N1}%" -f $within24, $MinWithin24)
    }

    $metricsPath = Join-Path $repo 'artifacts\demo\compare.json'
    [ordered]@{
        generatedAt        = (Get-Date).ToString('o')
        unityBrightness    = [Math]::Round($unityMean, 2)
        ueBrightness       = [Math]::Round($ueMean, 2)
        brightnessDelta    = [Math]::Round($brightnessDelta, 2)
        meanDiff           = [Math]::Round(($diffSum / $samples), 2)
        maxDiff            = [int]$maxDiff
        within24           = [Math]::Round($within24, 2)
        pixels             = [int]$samples
        maxBrightnessDelta = $MaxBrightnessDelta
        minWithin24        = $MinWithin24
        passed             = ($failed.Count -eq 0)
    } | ConvertTo-Json | Set-Content -Path $metricsPath -Encoding UTF8
    Write-Host "metrics: $metricsPath"

    if ($failed.Count -gt 0) {
        Write-Host ("compare thresholds failed: " + ($failed -join '; ')) -ForegroundColor Red
        exit 1
    }

    if (-not $NoPublish) {
        $publishedDiff = Join-Path $repo 'docs\images\demo-diff.png'
        Copy-Item $diffPath $publishedDiff -Force
        Write-Host "published diff: $publishedDiff"
    }
}

if (-not $NoPublish) {
    $published = Join-Path $repo 'docs\images\demo-compare.png'
    Copy-Item $compare $published -Force
    Write-Host "published: $published"
}
