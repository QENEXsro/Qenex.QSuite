#Requires -Version 7
<#
.SYNOPSIS
    Signs QENEX release artifacts with the company code-signing certificate.

.DESCRIPTION
    Uses the QENEX s.r.o. code-signing certificate stored on the YubiKey
    (slot 9a, exposed to Windows via the Yubico minidriver). The YubiKey
    must be plugged in. All files are signed by ONE signtool process, so Windows
    asks for the PIV PIN once per run (the PIN is cached per process; one
    signtool call per file would prompt for every file).
    Every signature gets an SSL.com timestamp so it outlives the certificate.

.EXAMPLE
    .\Sign-QInsight.ps1 D:\Projects\Qenex\Release\QInsight\QInsight-Setup-1.0.0.exe

.EXAMPLE
    .\Sign-QInsight.ps1 -Force bin\Release\QInsight.exe, bin\Release\Plugins\*.dll
#>
param(
    # Files to sign; wildcards allowed.
    [Parameter(Mandatory, Position = 0, ValueFromRemainingArguments)]
    [string[]]$Path,

    # Re-sign files already signed by our certificate.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$Thumbprint   = 'CC8A64B69DD3C3B6550AC0812D99D630C8425220'
$TimestampUrl = 'http://ts.ssl.com'

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
if (-not $signtool) { throw 'signtool.exe not found - install the Windows SDK.' }

if (-not (Test-Path "Cert:\CurrentUser\My\$Thumbprint")) {
    throw 'QENEX code-signing certificate not found in the user store - is the YubiKey plugged in?'
}

$files = $Path | ForEach-Object { Get-Item $_ } | Where-Object { -not $_.PSIsContainer }
if (-not $files) { throw "No files matched: $Path" }

$toSign  = [System.Collections.Generic.List[string]]::new()
$skipped = 0
foreach ($file in $files) {
    if (-not $Force) {
        $sig = Get-AuthenticodeSignature $file.FullName
        if ($sig.Status -eq 'Valid' -and $sig.SignerCertificate.Thumbprint -eq $Thumbprint) {
            Write-Host "SKIP  $($file.FullName) (already signed)" -ForegroundColor DarkGray
            $skipped++
            continue
        }
    }
    $toSign.Add($file.FullName)
}

if ($toSign.Count -gt 0) {
    # One signtool process for every file: a single PIN prompt. signtool signs the
    # files one after another and stops at the first failure.
    $signArgs = @('sign', '/sha1', $Thumbprint, '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256') + $toSign.ToArray()
    & $signtool.FullName @signArgs
    if ($LASTEXITCODE -ne 0) { throw "Signing failed (see signtool output above)" }

    foreach ($f in $toSign) {
        & $signtool.FullName verify /pa /q $f
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $f" }
        Write-Host "OK    $f" -ForegroundColor Green
    }
}
$signed = $toSign.Count

Write-Host "Signed $signed file(s), skipped $skipped." -ForegroundColor Cyan
