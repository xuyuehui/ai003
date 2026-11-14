# Shared gateway address / port helpers for the demo scripts.
#
# Why not Get-NetTCPConnection: inside a restricted (sandboxed) shell that cmdlet
# fails with "access denied"; combined with -ErrorAction SilentlyContinue that
# silently becomes "no listener", i.e. a wrong answer about whether a port is
# taken. netstat -ano works in every environment this repo is verified in.
#
# Dot-source it:  . (Join-Path $PSScriptRoot '_ports.ps1')

function Get-ListeningPid {
    param([Parameter(Mandatory)][int]$Port)

    $lines = & netstat -ano -p TCP 2>$null
    foreach ($line in $lines) {
        if ($line -match "^\s*TCP\s+\S+:$Port\s+\S+\s+LISTENING\s+(\d+)\s*$") {
            return [int]$Matches[1]
        }
    }
    return $null
}

function Test-PortListening {
    param([Parameter(Mandatory)][int]$Port)
    return ($null -ne (Get-ListeningPid -Port $Port))
}

# Stops the process listening on $Port, but only when it is the process we
# started ourselves. A port can be occupied by someone else's gateway (another
# demo run, a sibling bot); killing that listener would take down their work, so
# the safe default is to refuse: without -ExpectedPid nothing is killed and a
# warning says who holds the port.
function Stop-PortListener {
    param(
        [Parameter(Mandatory)][int]$Port,
        [int]$ExpectedPid = 0
    )
    $owner = Get-ListeningPid -Port $Port
    if ($null -eq $owner) { return $false }
    if ($ExpectedPid -le 0) {
        Write-Warning ("port {0} is held by PID {1}; not stopping it (pass -ExpectedPid with the PID you started)" -f $Port, $owner)
        return $false
    }
    if ($owner -ne $ExpectedPid) {
        Write-Warning ("port {0} is held by PID {1}, not by the expected PID {2}; not stopping it" -f $Port, $owner, $ExpectedPid)
        return $false
    }
    taskkill /PID $owner /T /F 2>&1 | Out-Null
    return $true
}

function ConvertTo-WsUrl {
    param([Parameter(Mandatory)][string]$BaseUrl)
    $ws = ($BaseUrl -replace '^http', 'ws').TrimEnd('/')
    if ($ws -notmatch '/ws$') { $ws = "$ws/ws" }
    return $ws
}

function ConvertTo-BaseUrl {
    param([Parameter(Mandatory)][string]$Url)
    return (($Url -replace '^ws', 'http') -replace '/ws$', '').TrimEnd('/')
}

function Get-HealthUrl {
    param([Parameter(Mandatory)][string]$Url)
    if ($Url -match '^ws') { return (ConvertTo-BaseUrl $Url) + '/healthz' }
    return $Url.TrimEnd('/') + '/healthz'
}
