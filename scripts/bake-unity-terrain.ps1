#!/usr/bin/env pwsh
# Rebuilds the baked terrain (16x16 chunks + colour texture) and triggers the
# SubScene bake. Batch mode needs the RunBatch state machine because a plain
# reimport falls back to DefaultImporter.
[CmdletBinding()]
param(
    [string]$Unity = 'C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe',
    [string]$Project = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($Project)) { $Project = Join-Path $root 'clients\unity\UnityGameClient' }
$log = Join-Path $root 'artifacts\logs\bake-unity-terrain.log'
New-Item -ItemType Directory -Force -Path (Split-Path $log -Parent) | Out-Null

Write-Host "==> baking terrain into $Project" -ForegroundColor Cyan
& $Unity -batchmode -projectPath $Project -executeMethod AI003.EditorTools.BakeTerrainSubScene.RunBatch -logFile $log
# Unity batch sometimes reports a non-zero exit even after a successful bake
# (observed on 6000.6.6f1); the cache artifact is the source of truth.
if ($LASTEXITCODE -ne 0) {
    Write-Host "unity exited $LASTEXITCODE; checking bake artifact" -ForegroundColor Yellow
}

$cache = Join-Path $Project 'Assets\SceneDependencyCache'
if (-not (Test-Path $cache)) {
    Get-Content $log -Tail 30 | ForEach-Object { Write-Host $_ }
    throw "terrain bake failed: $cache is missing"
}
Write-Host "terrain baked: $cache" -ForegroundColor Green
