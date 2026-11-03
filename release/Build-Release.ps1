param(
    [string]$Version = '0.30.1',
    [string]$SignTool = '',
    [string]$CertificateThumbprint = '',
    [switch]$AllowUnsignedDev
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$tag = 'v' + $Version
$archiveName = "ProstoKVN-$tag-win-x64.zip"
$dist = Join-Path $root 'dist'
$out = Join-Path $dist ("ProstoKVN-$tag-win-x64")
$zip = Join-Path $dist $archiveName
$old = Join-Path $dist 'ProstoKVN-v0.28-subscription-fix'

if (-not $AllowUnsignedDev -and (-not $SignTool -or -not $CertificateThumbprint)) {
    throw 'Для публичного релиза необходим Authenticode. Укажи -SignTool и -CertificateThumbprint. Для локальной проверки используй -AllowUnsignedDev.'
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Format: major.minor.patch' }
if (-not (Test-Path (Join-Path $old 'core'))) { throw 'Не найдены ядра, сначала собери базовую v0.28.' }
if (Test-Path $out) { throw "Папка релиза уже существует: $out (удаление автоматически запрещено)." }
if (Test-Path $zip) { throw "Архив релиза уже существует: $zip" }
New-Item -ItemType Directory -Path $out -Force | Out-Null
Push-Location $root
try {
    dotnet publish '.\ProstoKVN\ProstoKVN.csproj' -c Release -r win-x64 --self-contained false -o $out --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Сборка клиента не удалась.' }
    $helper = Join-Path $out 'updater'
    New-Item -ItemType Directory -Path $helper -Force | Out-Null
    dotnet publish '.\Updater\ProstoKVN.Updater.csproj' -c Release -r win-x64 --self-contained false -o $helper --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Сборка обновлятора не удалась.' }
    Copy-Item (Join-Path $old 'core') (Join-Path $out 'core') -Recurse
    Copy-Item (Join-Path $old 'licenses') (Join-Path $out 'licenses') -Recurse
    Copy-Item (Join-Path $root 'release\licenses\YamlDotNet-MIT.txt') (Join-Path $out 'licenses\YamlDotNet-MIT.txt')
    if (-not $AllowUnsignedDev) {
        foreach ($file in @(
            (Join-Path $out 'ProstoKVN.exe'),
            (Join-Path $helper 'ProstoKVN.Updater.exe')
        )) {
            & $SignTool sign /fd SHA256 /sha1 $CertificateThumbprint /tr 'http://timestamp.digicert.com' /td SHA256 $file
            if ($LASTEXITCODE -ne 0) { throw "Подпись не удалась: $file" }
            & $SignTool verify /pa /v $file
            if ($LASTEXITCODE -ne 0) { throw "Подпись недействительна: $file" }
        }
    }
    if (-not (Test-Path (Join-Path $out 'updater\ProstoKVN.Updater.exe'))) {
        throw 'Не найден updater exe.'
    }
    $notes = @"
ProstoKVN $tag — Windows x64
Обновления: GitHub Releases + проверка SHA-256 + отдельный updater с резервной копией.
Зависимость: Microsoft .NET Desktop Runtime 10.
Не отключает проверку сертификатов, SmartScreen или Defender.
"@
    Set-Content -Path (Join-Path $out 'RELEASE.txt') -Encoding UTF8 -Value $notes
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
    $sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path ($zip+'.sha256') -Value "$sha  $archiveName" -Encoding ascii
    Write-Output ('RELEASE_ARCHIVE='+$zip)
    Write-Output ('SHA256='+$sha)
    Write-Output ('RELEASE_STATUS=' + $(if ($AllowUnsignedDev) { 'UNSIGNED_DEV_ONLY' } else { 'AUTHENTICODE_SIGNED' }))
}
finally { Pop-Location }
