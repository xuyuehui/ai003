#!/usr/bin/env pwsh
# Generates the shared world and copies it into both clients' asset folders.
#
# The two engines never re-run the noise themselves: they load exactly the
# bytes produced here, which is what keeps their terrain identical.
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
$ueDir = Join-Path $root 'clients\ue5\UE5GameClient\WorldData'

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

foreach ($dir in @($unityDir, $ueDir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    foreach ($file in @('world.json', 'heightmap.f32', 'colors.rgba8', 'biomes.r8')) {
        Copy-Item (Join-Path $OutDir $file) (Join-Path $dir $file) -Force
    }
}

$hash = (Get-FileHash (Join-Path $OutDir 'heightmap.f32') -Algorithm SHA256).Hash
Write-Host ''
Write-Host "world copied to:" -ForegroundColor Green
Write-Host "  $unityDir"
Write-Host "  $ueDir"
Write-Host "heightmap sha256: $hash"
