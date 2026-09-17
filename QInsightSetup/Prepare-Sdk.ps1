#Requires -Version 7
<#
.SYNOPSIS
    Unpacks and verifies the QFW XCP SDK release zip for the QInsight installer.

.DESCRIPTION
    The QInsight installer (QInsightSetup.nsi) bundles the QFW XCP SDK as an
    unpacked source tree taken from
        D:\Projects\Qenex\Release\qfw-xcp-sdk\unpacked\qfw-xcp-sdk-<version>\
    This script produces that folder from the released zip
        D:\Projects\Qenex\Release\qfw-xcp-sdk\qfw-xcp-sdk-<version>.zip
    and refuses to continue unless
      1. the zip SHA-256 matches qfw-xcp-sdk-<version>.zip.sha256, and
      2. every unpacked file matches the SHA-256 listed in the package's
         MANIFEST.json (and no file is missing or extra).
    The version defaults to SDK_VERSION defined in QInsightSetup.nsi, so the
    .nsi stays the single place where the bundled SDK version is set.

.EXAMPLE
    .\Prepare-Sdk.ps1            # version from QInsightSetup.nsi
    .\Prepare-Sdk.ps1 -Version 1.0.0
#>
param(
    # SDK version to unpack; default = SDK_VERSION from QInsightSetup.nsi.
    [string]$Version,

    # Folder with the released zips (and the unpacked\ output).
    [string]$ReleaseDir = 'D:\Projects\Qenex\Release\qfw-xcp-sdk'
)

$ErrorActionPreference = 'Stop'

if (-not $Version) {
    $nsi = Join-Path $PSScriptRoot 'QInsightSetup.nsi'
    $m = Select-String -Path $nsi -Pattern '^!define\s+SDK_VERSION\s+"([^"]+)"' | Select-Object -First 1
    if (-not $m) { throw "SDK_VERSION not found in $nsi" }
    $Version = $m.Matches[0].Groups[1].Value
}

$name     = "qfw-xcp-sdk-$Version"
$zip      = Join-Path $ReleaseDir "$name.zip"
$shaFile  = "$zip.sha256"
$unpacked = Join-Path $ReleaseDir 'unpacked'
$target   = Join-Path $unpacked $name

if (-not (Test-Path $zip))     { throw "SDK zip not found: $zip" }
if (-not (Test-Path $shaFile)) { throw "SDK checksum file not found: $shaFile" }

# 1) zip checksum (file format: "<sha256> *<name>")
$expected = ((Get-Content $shaFile -Raw) -split '\s+')[0].ToLowerInvariant()
$actual   = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch for $zip`n expected $expected`n actual   $actual" }
Write-Host "OK    zip sha256 $actual" -ForegroundColor Green

# 2) unpack (fresh folder every time)
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Force $unpacked | Out-Null
Expand-Archive -Path $zip -DestinationPath $unpacked -Force
if (-not (Test-Path (Join-Path $target 'MANIFEST.json'))) {
    throw "Unpacked package does not contain MANIFEST.json: $target (zip layout changed?)"
}

# 3) per-file verification against MANIFEST.json
$manifest = Get-Content (Join-Path $target 'MANIFEST.json') -Raw | ConvertFrom-Json -AsHashtable
if ($manifest.package.version -ne $Version) {
    throw "MANIFEST.json version $($manifest.package.version) does not match requested $Version"
}
$listed = $manifest.files
$onDisk = Get-ChildItem $target -Recurse -File |
    Where-Object { $_.Name -ne 'MANIFEST.json' } |
    ForEach-Object { $_.FullName.Substring($target.Length + 1).Replace('\', '/') }

$bad = @()
foreach ($rel in $listed.Keys) {
    $f = Join-Path $target ($rel.Replace('/', '\'))
    if (-not (Test-Path $f)) { $bad += "missing: $rel"; continue }
    $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($h -ne $listed[$rel].ToLowerInvariant()) { $bad += "hash mismatch: $rel" }
}
foreach ($rel in $onDisk) {
    if (-not $listed.ContainsKey($rel)) { $bad += "not in manifest: $rel" }
}
if ($bad) { $bad | ForEach-Object { Write-Host "FAIL  $_" -ForegroundColor Red }; throw "SDK package verification failed ($($bad.Count) problem(s))" }

$size = (Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("OK    {0} files verified against MANIFEST.json, {1:N1} MB unpacked" -f ($listed.Count + 1), ($size / 1MB)) -ForegroundColor Green
Write-Host "READY $target" -ForegroundColor Cyan
