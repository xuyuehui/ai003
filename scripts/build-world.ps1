param(
    [int]$Size = 2048,
    [int]$Resolution = 0,
    [string]$OutDir = '',
    [switch]$NoClients
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
if ($Resolution -le 0) { $Resolution = [int]([math]::Round($Size / 4)) + 1 }
if (($Resolution - 1) % 2 -ne 0) { throw 'resolution must be odd: cells must be even' }
if (($Resolution - 1) % 32 -ne 0) { throw 'resolution - 1 must be divisible by 32' }

if (-not $OutDir) { $OutDir = Join-Path $repo 'world' }
Write-Host "==> generating world $Size m / $Resolution vertices -> $OutDir" -ForegroundColor Cyan

Push-Location (Join-Path $repo 'server')
try {
    & go run ./cmd/worldgen -size $Size -res $Resolution -out $OutDir
    if ($LASTEXITCODE -ne 0) { throw 'worldgen failed' }
}
finally {
    Pop-Location
}

if (-not $NoClients) {
    $targets = @(
        (Join-Path $repo 'clients\unity\UnityGameClient\Assets\StreamingAssets\world'),
        (Join-Path $repo 'clients\ue5\UE5GameClient\WorldData')
    )
    foreach ($target in $targets) {
        if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target | Out-Null }
        Get-ChildItem $OutDir -File | Where-Object { $_.Name -ne 'world.meta' } | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $target $_.Name) -Force
        }
        Write-Host "copied to $target"
    }
}

$meta = Get-Content (Join-Path $OutDir 'world.json') -Raw | ConvertFrom-Json
Write-Host ("world ready: {0} m, {1}x{1} vertices, {2} chunks of 128 m" -f `
    $meta.sizeMeters, $meta.resolution, [math]::Ceiling($meta.sizeMeters / 128))

