$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
$repo='GRESHNIK2019/ProstoKVN'
$tag='v0.29.1'
$commit='main'
$folder='D:\VEGACRMP_DEV\tools\vega_vpn_client\dist'
$zip=Join-Path $folder 'ProstoKVN-v0.29.1-win-x64.zip'
$sum=$zip+'.sha256'
if(!(Test-Path $zip) -or !(Test-Path $sum)){throw 'Release assets missing'}
$expected=(Get-Content $sum -Raw).Trim().Split(' ')[0].ToLowerInvariant()
$actual=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if($expected -ne $actual){throw 'Archive/checksum mismatch; abort'}
if((Get-Item $zip).Length -gt 220MB){throw 'Oversize release'}
$env:GCM_INTERACTIVE='never'
$inputData= @('protocol=https','host=github.com','') -join ([char]10)
$raw= $inputData | git credential fill 2>$null
$lines=@($raw | Where-Object {$_ -like 'password=*'} | Select-Object -First 1)
if($lines.Count -ne 1){throw 'Git Credential Manager cannot authorize GitHub API'}
$token=([string]$lines[0]).Substring(9)
$raw=$null;$lines=$null
$headers=@{
  Authorization=('Bearer '+$token)
  Accept='application/vnd.github+json'
  'X-GitHub-Api-Version'='2022-11-28'
  'User-Agent'='ProstoKVN-release-publisher'
}
$api="https://api.github.com/repos/$repo"
$existing=$null
try{$existing=Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $headers -Method Get -TimeoutSec 30}
catch {
 $status=$_.Exception.Response.StatusCode.value__
 if($status -ne 404){throw ('Cannot read release tag: HTTP '+$status)}
}
if($existing -and !$existing.draft){throw ('Release already public: '+$existing.html_url)}
$release=$existing
if(!$release){
  $notes=@'
ProstoKVN 0.29.1 — релиз с поддержкой обновлений через GitHub Releases.

- Проверка новых стабильных версий в настройках.
- Загрузка обновления после подтверждения пользователя.
- Проверка архива по SHA-256, защита от небезопасных ZIP-путей.
- Отдельный обновлятор перезапускает программу и пытается откатить файлы при ошибке.
- Сохранены интеллектуальная маршрутизация, тёмная тема, Discord UDP, автоподгрузка подписки.

Внимание: эта версия пока НЕ подписана сертификатом Authenticode. Windows SmartScreen/антивирус могут предупреждать о малоизвестном издателе. Не отключайте защиту Windows. Сверяйте SHA-256 с прилагаемым файлом. Для работы нужен .NET Desktop Runtime 10.

---
ProstoKVN 0.29.1 — a release with updates through GitHub Releases.

- Check for new stable versions inside Settings.
- User-approved download, SHA-256 validation and guarded ZIP extraction.
- Separate updater restarts the client and attempts rollback on failed installation.
- Includes smart split tunneling, dark theme, Discord UDP routing and subscription auto-import.

Warning: this build is currently UNSIGNED. Windows SmartScreen or antivirus software may show reputation warnings. Do not disable security software. Verify SHA-256 before running. Requires .NET Desktop Runtime 10.
'@
  $body=@{
    tag_name=$tag
    target_commitish=$commit
    name='ProstoKVN 0.29.1 — First Release'
    body=$notes
    draft=$true
    prerelease=$false
    generate_release_notes=$false
  }|ConvertTo-Json -Depth 8
  $release=Invoke-RestMethod -Uri "$api/releases" -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 60
  'DRAFT_CREATED='+$release.html_url
}
foreach($file in @($zip,$sum)){
  $name=[IO.Path]::GetFileName($file)
  $check=@($release.assets|Where-Object{$_.name -eq $name})
  if($check.Count -gt 0){
    if([long]$check[0].size -ne (Get-Item $file).Length){throw ('Existing asset size mismatch: '+$name)}
    'ALREADY_UPLOADED='+$name
    continue
  }
  $base=$release.upload_url -replace '\{\?name,label\}$',''
  $url=$base+'?name='+[uri]::EscapeDataString($name)
  $type=if($name -like '*.zip'){'application/zip'}else{'text/plain'}
  $asset=Invoke-RestMethod -Uri $url -Method Post -Headers $headers -ContentType $type -InFile $file -TimeoutSec 420
  if($asset.state -ne 'uploaded' -or [long]$asset.size -ne (Get-Item $file).Length){
    throw ('Asset verification failed: '+$name)
  }
  'ASSET_UPLOADED='+$name+' '+$asset.size
  $release=Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Headers $headers -Method Get -TimeoutSec 30
}
foreach($required in @([IO.Path]::GetFileName($zip),[IO.Path]::GetFileName($sum))){
  if(@($release.assets|Where-Object{$_.name -eq $required -and $_.state -eq 'uploaded'}).Count -ne 1){
    throw ('Required asset missing before publication: '+$required)
  }
}
if($release.draft){
  $body='{"draft":false,"prerelease":false,"make_latest":"true"}'
  $release=Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Headers $headers -Method Patch -ContentType 'application/json' -Body $body -TimeoutSec 60
}
$latest=Invoke-RestMethod -Uri "$api/releases/latest" -Headers $headers -Method Get -TimeoutSec 30
if($latest.tag_name -ne $tag -or $latest.draft){throw 'Latest stable release verification failed'}
if(@($latest.assets|Where-Object{$_.name -eq [IO.Path]::GetFileName($zip)}).Count -ne 1){throw 'ZIP absent from latest release'}
'RELEASE_PUBLISHED='+$latest.html_url
'TAG='+$latest.tag_name
'ASSETS='+(@($latest.assets|ForEach-Object{$_.name}) -join ', ')
'SHA256='+$actual
$token=$null;$headers=$null
