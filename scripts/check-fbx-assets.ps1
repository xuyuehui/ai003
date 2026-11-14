#!/usr/bin/env pwsh
# Delivery gate for the vegetation FBX sources.
#
#   scripts\check-fbx-assets.cmd
#   scripts\check-fbx-assets.cmd -Root <dir>      # another tree (e.g. a delivery)
#   scripts\check-fbx-assets.cmd -SelfTest        # prove the gate can still fail
#
# Why this exists: on 2026-11-14 a delivery of 9 FBX files reached the repo with a
# broken binary footer. Unity answered only "Couldn't import ... File is corrupted"
# and the models silently went missing. The first version of this gate checked just
# the trailing magic and passed all 9 broken files - so it now checks the whole
# footer, which is what Unity's FBX SDK actually needs:
#
#   end of the node section
#   [footer id  16 bytes  FA BC AB 09 D0 C8 D4 66 B1 76 FB 83 1C F7 26 7E]
#   [6..20 zero bytes - padding, varies per file]
#   [version    4 bytes   uint32 LE = 7400]
#   [120 zero bytes]
#   [magic     16 bytes   F85A 8C6A DEF5 D97E ECE9 0CE3 758F 290B]
#
# So the constants Unity relies on are anchored from the end: magic at -16,
# version at -140, 120 zeros between them. This is measured, not guessed: all 30
# in-repo models (imported fine by Unity) hit version at exactly -140 with 6..20
# zero bytes before it, and the broken delivery had neither.
#
# Exit 1 on any failure, so it can sit in CI or in front of a render.
[CmdletBinding()]
param(
    [string]$Root = '',
    [string]$Magic = 'F85A8C6ADEF5D97EECE90CE3758F290B',
    [int]$FooterVersion = 7400,
    # Regenerate the known-bad variants from a real model and require the gate to
    # reject every one of them. This is the negative fixture, derived so no corrupt
    # binary has to be committed.
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Root) { $Root = Join-Path $repoRoot 'clients\unity\UnityGameClient\Assets\Art\Models' }
if (-not (Test-Path $Root)) { throw "model root not found: $Root" }

$headerExpected = 'Kaydara FBX Binary  '
$magicBytes = [byte[]]($Magic -split '(?<=\G..)' | Where-Object { $_ } | ForEach-Object { [Convert]::ToByte($_, 16) })

# The FBX footer id is part of the spec, so it can be written down as a constant.
$footerIdHex = 'FABCAB09D0C8D466B176FB831CF7267E'
$footerIdBytes = [byte[]]($footerIdHex -split '(?<=\G..)' | Where-Object { $_ } | ForEach-Object { [Convert]::ToByte($_, 16) })

function Find-LastSequence([byte[]]$Haystack, [byte[]]$Needle) {
    for ($i = $Haystack.Length - $Needle.Length; $i -ge 0; $i--) {
        $hit = $true
        for ($j = 0; $j -lt $Needle.Length; $j++) {
            if ($Haystack[$i + $j] -ne $Needle[$j]) { $hit = $false; break }
        }
        if ($hit) { return $i }
    }
    return -1
}

function Test-FbxFooter([byte[]]$Bytes) {
    $problems = New-Object System.Collections.Generic.List[string]
    $len = $Bytes.Length

    if ($len -lt 200) {
        $problems.Add("file too short ($len bytes)")
        return $problems
    }

    # trailing magic
    for ($k = 0; $k -lt 16; $k++) {
        if ($Bytes[$len - 16 + $k] -ne $magicBytes[$k]) {
            $problems.Add('footer magic missing at end of file')
            break
        }
    }
    if ($problems.Count -gt 0) { return $problems }

    # version uint32 LE at exactly -140
    $version = [BitConverter]::ToUInt32($Bytes, $len - 140)
    if ($version -ne $FooterVersion) {
        $problems.Add("footer version at -140 is $version, expected $FooterVersion")
    }

    # 120 zero bytes between version and magic
    for ($k = $len - 136; $k -lt $len - 16; $k++) {
        if ($Bytes[$k] -ne 0) {
            $problems.Add("non-zero byte in the 120-byte zero run before the magic (offset $k)")
            break
        }
    }

    # footer id, within the window where it can legally sit
    $idPos = Find-LastSequence $Bytes $footerIdBytes
    if ($idPos -lt 0) {
        $problems.Add('footer id FABCAB09... missing (footer truncated to the magic alone?)')
    }
    else {
        if ($idPos -gt $len - 140) {
            $problems.Add("footer id at $idPos is after the version field - footer order wrong")
        }
        elseif ($idPos -lt $len - 220) {
            $problems.Add("footer id at $idPos is too far from the end to be the footer")
        }
        else {
            for ($k = $idPos + 16; $k -lt $len - 140; $k++) {
                if ($Bytes[$k] -ne 0) {
                    $problems.Add("non-zero byte between footer id and version (offset $k)")
                    break
                }
            }
        }
    }

    return $problems
}

function Test-FbxAsset([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    $problems = New-Object System.Collections.Generic.List[string]

    if ($file.Length -lt 200) {
        [pscustomobject]@{
            File = $file.FullName; Size = $file.Length; Verdict = 'FAIL'
            Detail = "file too short ($($file.Length) bytes)"
        }
        return
    }

    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)

    $header = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 20)
    if ($header -cne $headerExpected -or $bytes[20] -ne 0x00 -or $bytes[21] -ne 0x1A -or $bytes[22] -ne 0x00) {
        $problems.Add('bad header (not "Kaydara FBX Binary  " + 00 1A 00)')
    }

    foreach ($p in (Test-FbxFooter $bytes)) { $problems.Add($p) }

    [pscustomobject]@{
        File    = $file.FullName
        Size    = $file.Length
        Verdict = if ($problems.Count -eq 0) { 'PASS' } else { 'FAIL' }
        Detail  = ($problems -join '; ')
    }
}

function Invoke-Gate([string]$Path) {
    $files = @(Get-ChildItem -Path $Path -Recurse -Filter '*.fbx' -File | Sort-Object FullName)
    if ($files.Count -eq 0) { throw "no .fbx under $Path" }

    $rows = @($files | ForEach-Object { Test-FbxAsset $_.FullName })
    $failed = @($rows | Where-Object { $_.Verdict -ne 'PASS' })

    if ($failed.Count -gt 0) {
        $failed | Format-Table File, Size, Verdict, Detail -AutoSize -Wrap | Out-String -Width 4096 | Write-Host
    }
    else {
        $rows | Format-Table File, Size, Verdict -AutoSize | Out-String -Width 4096 | Write-Host
    }

    Write-Host ("==> {0}/{1} fbx PASS under {2}" -f ($rows.Count - $failed.Count), $rows.Count, $Path) `
        -ForegroundColor $(if ($failed.Count -eq 0) { 'Green' } else { 'Red' })
    return [pscustomobject]@{ Rows = $rows; Failed = $failed }
}

# ----------------------------------------------------------------- self test ---
function Invoke-SelfTest {
    $donor = Get-ChildItem -Path $Root -Recurse -Filter '*.fbx' -File |
        Sort-Object Length -Descending | Select-Object -First 1
    if (-not $donor) { throw 'self test needs at least one .fbx as a donor' }

    $dir = Join-Path $repoRoot 'artifacts\fbxcheck-selftest'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    Get-ChildItem $dir -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    $good = [System.IO.File]::ReadAllBytes($donor.FullName)
    $len = $good.Length

    # 1. the shape that actually shipped broken: node section + magic, no footer
    $onlyMagic = New-Object byte[] ($len - 156 + 16)
    [Array]::Copy($good, 0, $onlyMagic, 0, $len - 156)
    [Array]::Copy($magicBytes, 0, $onlyMagic, $onlyMagic.Length - 16, 16)
    [System.IO.File]::WriteAllBytes((Join-Path $dir 'bad-magic-only.fbx'), $onlyMagic)

    # 2. version field zeroed
    $badVersion = [byte[]]$good.Clone()
    for ($k = 0; $k -lt 4; $k++) { $badVersion[$len - 140 + $k] = 0 }
    [System.IO.File]::WriteAllBytes((Join-Path $dir 'bad-version-zero.fbx'), $badVersion)

    # 3. footer id zeroed
    $idPos = Find-LastSequence $good $footerIdBytes
    if ($idPos -lt 0) { throw 'self test: donor has no footer id to break' }
    $badId = [byte[]]$good.Clone()
    for ($k = 0; $k -lt 16; $k++) { $badId[$idPos + $k] = 0 }
    [System.IO.File]::WriteAllBytes((Join-Path $dir 'bad-id-zero.fbx'), $badId)

    # 4. extra zero byte injected into the 120-byte run
    $badRun = New-Object byte[] ($len + 1)
    [Array]::Copy($good, 0, $badRun, 0, $len - 60)
    $badRun[$len - 60] = 1
    [Array]::Copy($good, $len - 60, $badRun, $len - 59, 60)
    [System.IO.File]::WriteAllBytes((Join-Path $dir 'bad-run-dirty.fbx'), $badRun)

    Write-Host ''
    Write-Host "self test: bad variants written to $dir (donor $($donor.Name), $len bytes)" -ForegroundColor Cyan
    $gate = Invoke-Gate $dir

    $mustFail = @('bad-magic-only.fbx', 'bad-version-zero.fbx', 'bad-id-zero.fbx', 'bad-run-dirty.fbx')
    $leaks = @()
    foreach ($name in $mustFail) {
        $row = $gate.Rows | Where-Object { (Split-Path $_.File -Leaf) -eq $name }
        if (-not $row) { $leaks += "$name not produced" }
        elseif ($row.Verdict -ne 'FAIL') { $leaks += "$name passed the gate" }
        else { Write-Host ("  caught {0,-22} {1}" -f $name, $row.Detail) -ForegroundColor DarkGray }
    }

    if ($leaks.Count -gt 0) {
        Write-Host ('self test FAILED: ' + ($leaks -join '; ')) -ForegroundColor Red
        return $false
    }
    Write-Host 'self test PASSED: all 4 known-bad variants rejected' -ForegroundColor Green
    return $true
}

# ---------------------------------------------------------------------- main ---
$gate = Invoke-Gate $Root
$ok = $gate.Failed.Count -eq 0

if ($SelfTest) {
    if (-not (Invoke-SelfTest)) { $ok = $false }
}

if (-not $ok) {
    Write-Host 'A corrupt FBX passes every later check and only fails inside Unity; fix the exporter before delivering.' -ForegroundColor Red
    exit 1
}
exit 0
