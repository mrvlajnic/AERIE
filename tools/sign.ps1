param(
    [Parameter(Mandatory = $true)][string[]]$Path
)

# Code signing for release builds. Dormant until a certificate is configured:
# set WINDOWS_CERT_BASE64 (a .pfx, base64) and WINDOWS_CERT_PASSWORD as
# repository secrets. Without them this script says so and exits cleanly, so
# local and fork builds still produce unsigned artifacts.
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:SIGN_CERT_BASE64)) {
    Write-Host "Signing skipped: WINDOWS_CERT_BASE64 is not set."
    return
}
if ([string]::IsNullOrWhiteSpace($env:SIGN_CERT_PASSWORD)) {
    throw "WINDOWS_CERT_BASE64 is set but WINDOWS_CERT_PASSWORD is empty."
}

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" `
    -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found (install the Windows SDK)." }

$pfx = Join-Path $env:RUNNER_TEMP ("aerie-sign-" + [guid]::NewGuid().ToString("N") + ".pfx")
try {
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGN_CERT_BASE64))
    foreach ($file in $Path) {
        $full = (Resolve-Path $file).Path
        & $signtool.FullName sign /fd sha256 /td sha256 `
            /tr "http://timestamp.digicert.com" /f $pfx /p $env:SIGN_CERT_PASSWORD $full
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $full" }
        & $signtool.FullName verify /pa $full
        if ($LASTEXITCODE -ne 0) { throw "signature verification failed for $full" }
        Write-Host "Signed $full"
    }
}
finally {
    Remove-Item $pfx -Force -ErrorAction SilentlyContinue
}
