param(
    [switch]$Json,
    [string]$BaseUrl = "http://127.0.0.1:8080"
)

$ErrorActionPreference = 'Stop'

try {
    $health = Invoke-RestMethod "$BaseUrl/healthz" -TimeoutSec 5
    $demo = Invoke-RestMethod "$BaseUrl/demo" -TimeoutSec 5
}
catch {
    Write-Host "server not reachable at $BaseUrl" -ForegroundColor Red
    Write-Host "start it with: scripts\build-server.cmd ; server\bin\ai003-server.exe" -ForegroundColor Yellow
    exit 1
}

if ($Json) {
    $demo | ConvertTo-Json -Depth 6
    exit 0
}

"{0}  protocol v{1}  uptime {2:N0}s  go {3}  goroutines {4}" -f `
    $demo.service, $demo.protocolVersion, $demo.uptimeSeconds, $demo.goVersion, $demo.goroutines | Write-Host
"world players : {0}" -f $demo.worldPlayers | Write-Host
"rooms         : {0}" -f @($demo.rooms).Count | Write-Host

foreach ($room in @($demo.rooms)) {
    "  - {0,-10} {1}/{2} players   tick {3}" -f $room.id, $room.players, $room.capacity, $room.tick | Write-Host
}

"traffic       : {0:N0} messages / {1:N0} bytes   world snapshots {2:N0}   room frames {3:N0}" -f `
    $demo.stats.messagesOut, $demo.stats.bytesOut, $demo.stats.worldSnapshots, $demo.stats.roomFrames | Write-Host
"combat events : attack {0:N0}   hit {1:N0}   death {2:N0}   respawn {3:N0}" -f `
    $demo.stats.attackEvents, $demo.stats.hitEvents, $demo.stats.deathEvents, $demo.stats.respawnEvents | Write-Host
