param(
    [string]$Version = '0.30.2',
    [string]$CertificateThumbprint = '',
    [switch]$TestSignature,
    [switch]$Unsigned
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root ("dist\ProstoKVN-v$Version-win-x64")
$compiler = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
$signTool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
if (-not (Test-Path $compiler)) { throw 'Inno Setup 6 compiler is not installed.' }
if (-not (Test-Path $source)) { throw 'Self-contained payload missing.' }
if ($TestSignature -and ($Unsigned -or $CertificateThumbprint)) { throw 'Select only one signing mode.' }
if (-not $TestSignature -and -not $Unsigned -and -not $CertificateThumbprint) {
    throw 'Provide -CertificateThumbprint for trusted signing or explicitly choose -TestSignature / -Unsigned.'
}
$certificate = $null
if ($TestSignature) {
    $subject = 'CN=ProstoKVN Development (TEST ONLY)'
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date).AddDays(1) -and $_.HasPrivateKey
    } | Select-Object -First 1
    if (-not $certificate) {
        $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2)
    }
    $CertificateThumbprint = $certificate.Thumbprint
}
if ($CertificateThumbprint) {
    if (-not (Test-Path $signTool)) { throw 'Windows SDK SignTool not found.' }
    $certificate = Get-Item ("Cert:\CurrentUser\My\" + $CertificateThumbprint) -ErrorAction SilentlyContinue
    if (-not $certificate -or -not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) {
        throw 'Signing certificate is missing, expired or has no private key.'
    }
    if (-not (@($certificate.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId }) -contains '1.3.6.1.5.5.7.3.3')) {
        throw 'Certificate does not permit Code Signing.'
    }
}
# Never touch the original ZIP/update package; signing changes binary hashes.
$mode = if ($TestSignature) { 'TEST-SIGNED' } elseif ($CertificateThumbprint) { 'SIGNED' } else { 'UNSIGNED' }
$stage = Join-Path $root ("dist\installer-stage-v$Version-$mode")
$outputName = "ProstoKVN-Setup-$Version-$mode"
$setup = Join-Path $root ("dist\$outputName.exe")
if ((Test-Path $stage) -or (Test-Path $setup)) {
    throw "Target already exists. No automatic overwrite: $stage or $setup"
}
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse -ErrorAction Stop
foreach ($required in @('ProstoKVN.exe','ProstoKVN.dll','hostfxr.dll','coreclr.dll','PresentationFramework.dll','updater\ProstoKVN.Updater.exe','core\mihomo.exe','core\sing-box.exe','core\xray.exe')) {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "Installer payload missing: $required" }
}
function Sign-File([string]$file) {
    if (-not $CertificateThumbprint) { return }
    $options = @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint, '/s', 'MY')
    if (-not $TestSignature) {
        $options += @('/tr','http://timestamp.digicert.com','/td','SHA256')
    }
    $options += $file
    & $signTool @options
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for: $file" }
    $signature = Get-AuthenticodeSignature -FilePath $file
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
        throw "Signature not attached to: $file"
    }
    if (-not $TestSignature -and $signature.Status -ne 'Valid') {
        throw "Production signature is not trusted: $file; $($signature.Status)"
    }
}
Sign-File (Join-Path $stage 'ProstoKVN.exe')
Sign-File (Join-Path $stage 'updater\ProstoKVN.Updater.exe')
& $compiler '/Qp' ("/DBuildDir=$stage") ("/F$outputName") (Join-Path $PSScriptRoot 'ProstoKVN.iss')
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $setup)) { throw 'Inno Setup compilation failed.' }
Sign-File $setup
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Encoding ascii -Value "$hash  $outputName.exe"
Write-Output "INSTALLER=$setup"
Write-Output "INSTALLER_BYTES=$((Get-Item $setup).Length)"
Write-Output "SIGNING_MODE=$mode"
Write-Output "CERT_SUBJECT=$(if ($certificate) {$certificate.Subject} else {'none'})"
Write-Output "SIGNATURE_STATUS=$((Get-AuthenticodeSignature $setup).Status)"
Write-Output "SHA256=$hash"
Write-Output 'USER_SETTINGS_PRESERVED=%LOCALAPPDATA%\ProstoKVN'
if ($TestSignature) { Write-Warning 'Development self-signed signature is NOT trusted by other users. SmartScreen warnings can still appear.' }