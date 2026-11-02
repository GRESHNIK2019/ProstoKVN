$ErrorActionPreference='Stop'
$src='D:\VEGACRMP_DEV\tools\vega_vpn_client'
$dest=Join-Path $env:TEMP ('ProstoKVN-publication-'+[Guid]::NewGuid().ToString('N'))
$env:GIT_TERMINAL_PROMPT='0'
git clone --quiet https://github.com/GRESHNIK2019/ProstoKVN.git $dest
if($LASTEXITCODE -ne 0){throw 'Cannot clone repository'}
# Stage removal of the obsolete tracked project folder in the Git index.
# Leave working copies untouched: index-only removal is sufficient.
$legacyTracked = @(git -C $dest ls-files 'VegaVpn/*')
foreach ($entry in $legacyTracked) {
    git -C $dest update-index --force-remove -- $entry
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage the obsolete file removal.' }
}
'LEGACY_TRACKED_REMOVALS='+$legacyTracked.Count
function Copy-Clean($folder,$extensionPattern){
    $base=Join-Path $src $folder
    $to=Join-Path $dest $folder
    New-Item $to -ItemType Directory -Force | Out-Null
    Get-ChildItem $base -File | Where-Object {
        $_.Name -notmatch '(\.before-|\.user$|\.bak$)' -and
        $_.Name -match $extensionPattern
    } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $to $_.Name)
    }
}
Copy-Clean 'ProstoKVN' '\.(cs|csproj|xaml)$'
Copy-Clean 'Updater' '\.(cs|csproj)$'
Copy-Clean 'SmokeTest' '\.(cs|csproj)$'
Copy-Clean 'release' '\.(ps1|iss|md)$'
foreach($folder in @('ProstoKVN\Themes','SmokeTest\UpdaterFixture')){
    $base=Join-Path $src $folder
    $to=Join-Path $dest $folder
    New-Item $to -ItemType Directory -Force|Out-Null
    Get-ChildItem $base -File | Where-Object {$_.Name -match '\.(cs|csproj|xaml)$'} |
        ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $to $_.Name)}
}
$ignore=@'
**/bin/
**/obj/
**/.vs/
dist/
core/
backup-*/
test-v*/
*.before-*
*.user
*.pdb
*.log
*.tmp
*.zip
*.zip.sha256
settings.json
routes.json
subscription-cache.json
config.yaml
.env
.env.*
!*.example
*.pfx
*.p12
*.key
*.pem
'@
Set-Content (Join-Path $dest '.gitignore') $ignore -Encoding utf8
$readme=@'
# ProstoKVN

**Русский:** ProstoKVN — VPN-клиент для Windows с интеллектуальной маршрутизацией приложений и сайтов. Поддерживает подписки 3x-ui, TUN, VLESS/REALITY, Hysteria2, Mihomo, sing-box и Xray-core. Можно направлять выбранные приложения через VPN, а остальные соединения — напрямую.

**English:** ProstoKVN is a Windows VPN client with intelligent per-app and per-domain split tunneling. Supports 3x-ui subscriptions, TUN, VLESS/REALITY, Hysteria2, Mihomo, sing-box and Xray-core. Route selected apps and domains through VPN while keeping other traffic direct.

## Download / Скачать
Use the official [GitHub Releases](https://github.com/GRESHNIK2019/ProstoKVN/releases) page. **The first update-enabled release is v0.29.1.** Versions after v0.29.1 are detected via GitHub Releases, downloaded after user approval, checked against a published SHA-256 digest, installed with a rollback attempt, then restarted.

**Notice / Внимание:** Builds currently distributed for development are unsigned. Windows SmartScreen or antivirus software may warn about unfamiliar binaries. Do not disable your protection software. Confirm release filenames and SHA-256 before running the download.

## Updates / Обновления
Open **Настройки → Обновления приложения → Проверить обновления**. Stable releases use tags vMAJOR.MINOR.PATCH with the following assets:
- ProstoKVN-vMAJOR.MINOR.PATCH-win-x64.zip
- ProstoKVN-vMAJOR.MINOR.PATCH-win-x64.zip.sha256

The installer keeps user settings and subscription URLs in %LOCALAPPDATA%\ProstoKVN outside the application directory. VPN connectivity is interrupted only when the new package is ready to install and the user approves the restart.

## Build / Сборка
- Windows 10/11 x64
- .NET 10 SDK and .NET Desktop Runtime 10
- dotnet build .\ProstoKVN\ProstoKVN.csproj -c Release
- dotnet build .\Updater\ProstoKVN.Updater.csproj -c Release

release/Build-Release.ps1 generates the ZIP, checksums, and a separately executable update helper. Publishing to users should use an Authenticode certificate; see [release/README.md](release/README.md). release/ProstoKVN.iss is an Inno Setup configuration (requires Inno Setup).

### Third-party dependencies
The packaged client uses Mihomo, sing-box, and Xray-core. Their own licenses are included with binary distributions. The ProstoKVN source is distributed under the license in [LICENSE](LICENSE); that does not replace requirements imposed by third-party licenses.

## Privacy / Приватность
Subscriptions and routing settings remain on the user's computer and must not be committed to this repository. The source repository intentionally does not contain personal settings, subscription links, API keys, or private certificates.
'@
Set-Content (Join-Path $dest 'README.md') $readme -Encoding utf8
$workflowDir=Join-Path $dest '.github\workflows'
New-Item $workflowDir -ItemType Directory -Force|Out-Null
$workflow=@'
name: Build ProstoKVN
on:
  push:
    branches: [main]
  pull_request:
    branches: [main]
permissions:
  contents: read
jobs:
  compile:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Build Windows client and updater
        shell: pwsh
        run: |
          dotnet build .\ProstoKVN\ProstoKVN.csproj -c Release
          dotnet build .\Updater\ProstoKVN.Updater.csproj -c Release
          dotnet build .\SmokeTest\SmokeTest.csproj -c Release
'@
Set-Content (Join-Path $workflowDir 'build.yml') $workflow -Encoding utf8
Push-Location $dest
try{
    git config user.name 'GRESHNIK2019'
    git config user.email '45656626+GRESHNIK2019@users.noreply.github.com'
    git add README.md .gitignore ProstoKVN Updater SmokeTest release .github
    $files=git diff --cached --name-only
    $sourceFiles=@($files)
    if($sourceFiles.Count -lt 40){throw "Expected at least 40 source files; got $($sourceFiles.Count)"}
    $bad=@($files|Where-Object {$_ -match '(bin/|obj/|\.before-|settings\.json|routes\.json|subscription-cache|\.pfx$|\.key$|\.zip$|\.exe$)'})
    if($bad.Count){ throw "Sensitive files staged: $($bad -join ',')" }
    $potentialLeaks=@()
    $regexes=@('ghp_[A-Za-z0-9]{20,}','github_pat_[A-Za-z0-9_]{20,}',
      'sk-proj-[A-Za-z0-9_-]{20,}','BEGIN (?:RSA|OPENSSH|EC) PRIVATE KEY',
      'https?://[^\s]+/(?:sub|json)/[A-Za-z0-9_-]{16,}')
    foreach($f in $files){
        if($f -match '\.(cs|ps1|md|xaml|json|yml|iss|csproj)$'){
            $data=Get-Content -LiteralPath (Join-Path $dest $f) -Raw
            foreach($rx in $regexes){if($data -match $rx){$potentialLeaks+= $f}}
        }
    }
    if($potentialLeaks.Count){throw "Potential secrets in: $($potentialLeaks -join ',')"}
    'STAGING='+$dest
    'FILES='+$sourceFiles.Count
    'STATUS=CONTENT_SCAN_PASS'
    git diff --cached --stat | Select-Object -Last 6
}
finally{Pop-Location}
