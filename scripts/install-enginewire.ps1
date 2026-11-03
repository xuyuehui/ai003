#!/usr/bin/env pwsh
# Installs the EngineWire editor plugin (UE MCP bridge) into the UE project.
# The plugin source is third-party and ~2.6 GB once built, so it is not committed.
[CmdletBinding()]
param(
    [string]$Version = '0.6.0',
    [string]$Engine = 'C:\Program Files\Epic Games\UE_5.8',
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root 'clients\ue5\UE5GameClient'
$project = Join-Path $projectDir 'UE5GameClient.uproject'
$plugins = Join-Path $projectDir 'Plugins'
$target = Join-Path $plugins 'EngineWire'
$downloads = Join-Path $root 'artifacts\downloads'
$zip = Join-Path $downloads "EngineWire-plugin-$Version.zip"
$url = "https://github.com/ChiR24/Unreal_mcp/releases/download/v$Version/EngineWire-plugin-$Version.zip"

New-Item -ItemType Directory -Force -Path $downloads | Out-Null

if (-not (Test-Path $target)) {
    if (-not (Test-Path $zip)) {
        Write-Host "==> Downloading $url" -ForegroundColor Cyan
        Invoke-WebRequest $url -OutFile $zip -TimeoutSec 300
    }

    $temp = Join-Path $downloads 'enginewire-extract'
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    Expand-Archive -Path $zip -DestinationPath $temp -Force

    $source = Get-ChildItem $temp -Recurse -Directory -Filter 'EngineWire' |
        Where-Object { Test-Path (Join-Path $_.FullName 'EngineWire.uplugin') } |
        Select-Object -First 1
    if (-not $source) { throw 'EngineWire.uplugin not found in the archive' }

    New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    Copy-Item $source.FullName $target -Recurse
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "installed plugin: $target" -ForegroundColor Green
}
else {
    Write-Host "plugin already present: $target"
}

# Enable the plugin in the project descriptor.
$json = Get-Content $project -Raw | ConvertFrom-Json
$names = @($json.Plugins | ForEach-Object { $_.Name })
if ($names -notcontains 'EngineWire') {
    $json.Plugins += [pscustomobject]@{ Name = 'EngineWire'; Enabled = $true }
    $json | ConvertTo-Json -Depth 8 | Set-Content $project -Encoding utf8
    Write-Host 'enabled EngineWire in UE5GameClient.uproject'
}

# Native MCP settings: loopback only, token off for the local DSH proxy.
$gameIni = Join-Path $projectDir 'Config\DefaultGame.ini'
$ini = Get-Content $gameIni -Raw
if ($ini -notmatch '\[/Script/EngineWire.EngineWireSettings\]') {
    Add-Content $gameIni @"

[/Script/EngineWire.EngineWireSettings]
; Local-only automation bridge used by the DSH MCP client.
bEnableNativeMCP=True
NativeMCPPort=3000
bRequireCapabilityToken=False
bAlwaysListen=False
"@
    Write-Host 'wrote EngineWire settings to Config\DefaultGame.ini'
}

if ($Build) {
    $buildBat = Join-Path $Engine 'Engine\Build\BatchFiles\Build.bat'
    Write-Host '==> Building UE5GameClientEditor with EngineWire (first build takes a while)' -ForegroundColor Cyan
    & $buildBat UE5GameClientEditor Win64 Development "-Project=$project" -WaitMutex -FromMsBuild
    if ($LASTEXITCODE -ne 0) { throw "UBT build failed with exit code $LASTEXITCODE" }
}

Write-Host ''
Write-Host 'EngineWire ready. Open the project, then the editor status bar shows "MCP :3000".' -ForegroundColor Green
