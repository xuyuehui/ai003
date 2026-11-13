#!/usr/bin/env pwsh
# Generates the world and copies it into the Unity client's asset folder.
#
# The engine never re-runs the noise itself: it loads exactly the bytes
# produced here, which is what keeps the terrain in the game identical.
[CmdletBinding()]
param(
    [string]$Seed = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'world' }

$serverDir = Join-Path $root 'server'
$unityDir = Join-Path $root 'clients\unity\UnityGameClient\Assets\StreamingAssets\world'

Push-Location $serverDir
try {
    $goArgs = @('run', './cmd/worldgen', '-out', $OutDir)
    if ($Seed) { $goArgs += @('-seed', $Seed) }
    & go @goArgs
    if ($LASTEXITCODE -ne 0) { throw "worldgen failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

New-Item -ItemType Directory -Force -Path $unityDir | Out-Null
foreach ($file in @('world.json', 'heightmap.f32', 'colors.rgba8', 'biomes.r8')) {
    Copy-Item (Join-Path $OutDir $file) (Join-Path $unityDir $file) -Force
}

$hash = (Get-FileHash (Join-Path $OutDir 'heightmap.f32') -Algorithm SHA256).Hash
Write-Host ''
Write-Host "world copied to:" -ForegroundColor Green
Write-Host "  $unityDir"
Write-Host "heightmap sha256: $hash"
