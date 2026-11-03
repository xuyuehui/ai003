#!/usr/bin/env pwsh
# Builds the Go server and runs its unit tests.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root 'server'
$binDir = Join-Path $server 'bin'

New-Item -ItemType Directory -Force -Path $binDir | Out-Null

Push-Location $server
try {
    Write-Host '==> go build ./...' -ForegroundColor Cyan
    go build ./...
    if ($LASTEXITCODE -ne 0) { throw 'go build failed' }

    Write-Host '==> go vet ./...' -ForegroundColor Cyan
    go vet ./...
    if ($LASTEXITCODE -ne 0) { throw 'go vet failed' }

    Write-Host '==> go test ./...' -ForegroundColor Cyan
    go test ./... -count=1
    if ($LASTEXITCODE -ne 0) { throw 'go test failed' }

    Write-Host '==> go build -o bin/ai003-server.exe' -ForegroundColor Cyan
    go build -o (Join-Path $binDir 'ai003-server.exe') ./cmd/server
    if ($LASTEXITCODE -ne 0) { throw 'server binary build failed' }
}
finally {
    Pop-Location
}

Write-Host "server built: $binDir\ai003-server.exe" -ForegroundColor Green
