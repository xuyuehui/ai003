#!/usr/bin/env pwsh
# Safety gate for scripts\_ports.ps1: Stop-PortListener may only stop the listener
# it was told to expect. A port can be occupied by someone else's gateway (another
# demo run, a sibling bot); before this gate, cleanup killed whatever was on 8080.
#
#   scripts\test-ports.cmd
#   scripts\test-ports.cmd -Port 8091      # another scratch port
#
# Cases are self-contained: fake TCP listeners on a scratch port (default 8090),
# killed between cases, and the 8080/8081 gateway is never touched. No Unity, no Go.
[CmdletBinding()]
param(
    [int]$Port = 8090
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot '_ports.ps1')

$results = New-Object System.Collections.Generic.List[object]
$children = New-Object System.Collections.Generic.List[object]

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Eq($Actual, $Expected, [string]$Message) {
    if ($Actual -ne $Expected) { throw "$Message (got '$Actual', want '$Expected')" }
}

function Start-Fake([int]$P) {
    # Inline listener: bind loopback, sit there so netstat reports a listener.
    $code = "`$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $P); `$l.Start(); Start-Sleep -Seconds 120"
    $enc = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($code))
    $proc = Start-Process -FilePath 'powershell' -PassThru -WindowStyle Hidden `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $enc)
    $children.Add($proc)
    for ($i = 0; $i -lt 80; $i++) {
        if ((Get-ListeningPid -Port $P) -eq [int]$proc.Id) { return $proc }
        if ($proc.HasExited) { throw "fake listener exited early (exit $($proc.ExitCode))" }
        Start-Sleep -Milliseconds 250
    }
    throw "fake listener never bound $P"
}

function Stop-Fake([object]$Proc) {
    if ($Proc -and -not $Proc.HasExited) {
        Stop-Process -Id $Proc.Id -Force -ErrorAction SilentlyContinue
        $Proc.WaitForExit(5000) | Out-Null
        for ($i = 0; $i -lt 40; $i++) {
            if ($null -eq (Get-ListeningPid -Port $Port)) { return }
            Start-Sleep -Milliseconds 250
        }
    }
}

function Invoke-Case([string]$Id, [string]$Title, [scriptblock]$Body) {
    $detail = ''
    try {
        $detail = (& $Body) -join '; '
        $results.Add([pscustomobject]@{ Id = $Id; Case = $Title; Verdict = 'PASS'; Detail = $detail })
        Write-Host "PASS $Id  $Title  $detail"
    }
    catch {
        $results.Add([pscustomobject]@{ Id = $Id; Case = $Title; Verdict = 'FAIL'; Detail = "$_" })
        Write-Host "FAIL $Id  $Title  -> $_" -ForegroundColor Red
    }
    finally {
        # never let one case's fake listener poison the next one
        $owner = Get-ListeningPid -Port $Port
        if ($null -ne $owner) {
            foreach ($c in $children) {
                if ([int]$c.Id -eq $owner -and -not $c.HasExited) {
                    Stop-Process -Id $c.Id -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Milliseconds 300
                }
            }
        }
    }
}

$existing = Get-ListeningPid -Port $Port
if ($null -ne $existing) { throw "port $Port already in use by PID $existing; pass another -Port" }

try {
    Invoke-Case 'C1' 'no -ExpectedPid: refuse, warn, listener survives' {
        $fake = Start-Fake $Port
        $all = @(Stop-PortListener -Port $Port 3>&1)
        $ret = @($all | Where-Object { $_ -is [bool] })[0]
        $warn = @($all | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } | ForEach-Object { $_.Message })
        Assert-Eq $ret $false 'return value'
        Assert-True ($warn.Count -ge 1) 'no warning emitted'
        Assert-True (($warn -join ' ') -match "held by PID $($fake.Id)") 'warning does not name the owner PID'
        Assert-Eq (Get-ListeningPid -Port $Port) ([int]$fake.Id) 'listener was killed'
        "return=False, warning='$($warn[0])', owner unchanged"
    }

    Invoke-Case 'C2' 'wrong -ExpectedPid: refuse, listener survives' {
        $fake = Start-Fake $Port
        $all = @(Stop-PortListener -Port $Port -ExpectedPid ($fake.Id + 1) 3>&1)
        $ret = @($all | Where-Object { $_ -is [bool] })[0]
        $warn = @($all | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } | ForEach-Object { $_.Message })
        Assert-Eq $ret $false 'return value'
        Assert-True ($warn.Count -ge 1) 'no warning emitted'
        Assert-Eq (Get-ListeningPid -Port $Port) ([int]$fake.Id) 'listener was killed'
        'return=False, listener alive'
    }

    Invoke-Case 'C3' 'stale PID (port taken over by another process): refuse' {
        $first = Start-Fake $Port
        $firstPid = [int]$first.Id
        Stop-Fake $first
        $second = Start-Fake $Port
        Assert-True ($second.Id -ne $firstPid) 'second fake reused the same PID'
        $all = @(Stop-PortListener -Port $Port -ExpectedPid $firstPid 3>&1)
        $ret = @($all | Where-Object { $_ -is [bool] })[0]
        Assert-Eq $ret $false 'return value'
        Assert-Eq (Get-ListeningPid -Port $Port) ([int]$second.Id) 'the new listener was killed'
        "caller-held PID $firstPid dead, new owner $($second.Id) untouched"
    }

    Invoke-Case 'C4' 'correct -ExpectedPid: kill, port freed' {
        $fake = Start-Fake $Port
        $ret = Stop-PortListener -Port $Port -ExpectedPid $fake.Id
        Start-Sleep -Milliseconds 500
        Assert-Eq $ret $true 'return value'
        Assert-True ($null -eq (Get-ListeningPid -Port $Port)) 'port still held after kill'
        "return=True, port $Port freed"
    }

    Invoke-Case 'C5' 'nothing listening: return False, no throw' {
        $ret = Stop-PortListener -Port $Port
        Assert-Eq $ret $false 'return value'
        'return=False'
    }

    Invoke-Case 'C6' 'explicit -ExpectedPid 0: refuse (same as absent)' {
        $fake = Start-Fake $Port
        $ret = Stop-PortListener -Port $Port -ExpectedPid 0 3>$null
        Assert-Eq $ret $false 'return value'
        Assert-Eq (Get-ListeningPid -Port $Port) ([int]$fake.Id) 'listener was killed'
        'return=False, listener alive'
    }

    Invoke-Case 'C7' 'negative -ExpectedPid: refuse' {
        $fake = Start-Fake $Port
        $ret = Stop-PortListener -Port $Port -ExpectedPid -1 3>$null
        Assert-Eq $ret $false 'return value'
        Assert-Eq (Get-ListeningPid -Port $Port) ([int]$fake.Id) 'listener was killed'
        'return=False, listener alive'
    }

    Invoke-Case 'C8' '-Port is mandatory (cannot be called without a target)' {
        $param = (Get-Command Stop-PortListener).Parameters['Port']
        $attr = @($param.Attributes | Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] })
        Assert-True ($attr.Count -ge 1) 'Port has no Parameter attribute'
        Assert-True ($attr[0].Mandatory) 'Port is not mandatory'
        'Port Mandatory=True'
    }

    Invoke-Case 'C9' 'static: every caller passes -ExpectedPid' {
        $bad = @()
        foreach ($f in Get-ChildItem (Join-Path $root 'scripts') -Filter '*.ps1') {
            # the gate itself calls without -ExpectedPid on purpose (C1/C2), and the
            # definition in _ports.ps1 is not a call site
            if ($f.Name -in @('test-ports.ps1', '_ports.ps1')) { continue }
            foreach ($m in Select-String -Path $f.FullName -Pattern 'Stop-PortListener\s+-Port') {
                if ($m.Line -notmatch '-ExpectedPid') { $bad += "$($f.Name):$($m.LineNumber)" }
            }
        }
        Assert-True ($bad.Count -eq 0) ("callers without -ExpectedPid: " + ($bad -join ', '))
        'all Stop-PortListener call sites pass -ExpectedPid'
    }
}
finally {
    foreach ($proc in $children) {
        if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ''
$results | Format-Table Id, Case, Verdict -AutoSize | Out-String -Width 4096 | Write-Host
$failed = @($results | Where-Object { $_.Verdict -ne 'PASS' })
Write-Host ("==> {0}/{1} port-helper cases PASS" -f ($results.Count - $failed.Count), $results.Count) `
    -ForegroundColor $(if ($failed.Count -eq 0) { 'Green' } else { 'Red' })
if ($failed.Count -gt 0) { exit 1 }
exit 0
