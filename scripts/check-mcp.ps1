#!/usr/bin/env pwsh
# Prints the local state of both engine MCP bridges and, when reachable,
# probes the Unity server for its tool list.
[CmdletBinding()]
param(
    [switch]$ProbeUnity
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot

function Test-Port([int]$Port) {
    $conn = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalPort -eq $Port } | Select-Object -First 1
    if ($conn) { return $conn.OwningProcess } else { return $null }
}

$uePid = Test-Port 3000
$unityPid = Test-Port 6400

"Unreal MCP (EngineWire, http://127.0.0.1:3000) : " + $(if ($uePid) { "listening (pid $uePid)" } else { 'DOWN - start UnrealEditor with the project' })
"Unity  MCP (bridge,      tcp://127.0.0.1:6400)   : " + $(if ($unityPid) { "listening (pid $unityPid)" } else { 'DOWN - start the Unity editor (auto-start bridge)' })

if ($ProbeUnity -and $unityPid) {
    Write-Host '==> Probing Unity MCP server (tools/list)' -ForegroundColor Cyan
    $env:UNITY_MCP_CONNECTION_TIMEOUT = '20'
    $env:UNITY_MCP_COMMAND_TOTAL_TIMEOUT = '120'
    $env:UNITY_MCP_TIMEOUT = '30'
    node (Join-Path $root 'scripts\mcp-probe.mjs') 'C:\Users\xuyuehui\.local\bin\mcp-for-unity.exe' --transport stdio
    exit $LASTEXITCODE
}

if (-not $uePid) {
    Write-Host 'Start it with: & "$env:ProgramFiles\Epic Games\UE_5.8\Engine\Binaries\Win64\UnrealEditor.exe" "clients\ue5\UE5GameClient\UE5GameClient.uproject"'
}
if (-not $unityPid) {
    Write-Host 'Start it with: & "C:\Program Files\Unity 6000.6.6f1\Editor\Unity.exe" -projectPath "clients\unity\UnityGameClient"'
}
