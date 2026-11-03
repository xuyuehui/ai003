#!/usr/bin/env pwsh
# Creates the /Game/AI003 materials with the Unreal Python API (one-off).
[CmdletBinding()]
param(
    [string]$Engine = 'C:\Program Files\Epic Games\UE_5.8'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'clients\ue5\UE5GameClient\UE5GameClient.uproject'
$script = Join-Path $root 'clients\ue5\UE5GameClient\Scripts\setup_assets.py'
$assets = Join-Path $root 'clients\ue5\UE5GameClient\Content\AI003'
$editor = Join-Path $Engine 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
$log = Join-Path $root 'artifacts\logs\ue-assets.log'

if (-not (Test-Path $editor)) { throw "UnrealEditor-Cmd.exe not found under $Engine" }
New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
Remove-Item $log -Force -ErrorAction SilentlyContinue

Write-Host '==> Creating UE materials (Python commandlet)' -ForegroundColor Cyan
& $editor $project '-run=PythonScript' "-script=$script" -unattended -nosplash -nullrhi -nop4 -stdout 2>&1 |
    Tee-Object -FilePath $log | Out-Null
$exitCode = $LASTEXITCODE

$text = ''
if (Test-Path $log) { $text = Get-Content $log -Raw }
$executed = $text -match 'Python script executed successfully'

$expected = @('M_TerrainVertexColor.uasset', 'M_Ocean.uasset', 'M_PlayerBody.uasset', 'M_PlayerAccent.uasset')
$missing = @($expected | Where-Object { -not (Test-Path (Join-Path $assets $_)) })

if ($exitCode -ne 0 -or -not $executed -or $missing.Count -gt 0) {
    if (Test-Path $log) { Get-Content $log -Tail 30 }
    throw "material creation failed (exit=$exitCode, executed=$executed, missing=$($missing -join ',')); see $log"
}

Write-Host 'UE materials ready: /Game/AI003/M_TerrainVertexColor, M_Ocean, M_PlayerBody, M_PlayerAccent' -ForegroundColor Green
