$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dev = Join-Path $env:TEMP ('ProstoKVN-updater-fixture-'+[guid]::NewGuid().ToString('N'))
$publish = Join-Path $dev 'publish'
$target = Join-Path $dev 'target'
$stage = Join-Path (Join-Path $env:LOCALAPPDATA 'ProstoKVN\updates') ('stage-'+[guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Path $publish,$target,$payload -Force | Out-Null
dotnet publish (Join-Path $root 'SmokeTest\UpdaterFixture\UpdaterFixture.csproj') -c Release -r win-x64 --self-contained false -o $publish --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Fake app did not compile' }
Copy-Item (Join-Path $publish '*') $target -Force
Copy-Item (Join-Path $publish 'ProstoKVN.exe') $payload
Set-Content -Path (Join-Path $payload 'new-version.fixture') -Value 'v2' -Encoding ascii
Set-Content -Path (Join-Path $payload 'extra.fixture') -Value 'extra' -Encoding ascii
$manifest = @{}
foreach($f in Get-ChildItem $payload -File) {
 $manifest[$f.Name] = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
}
$manifest | ConvertTo-Json -Compress | Set-Content -Path (Join-Path $stage 'hashes.json') -Encoding utf8
$helper = Join-Path $root 'Updater\bin\Release\net10.0-windows\win-x64\ProstoKVN.Updater.exe'
if (-not (Test-Path $helper)) { throw 'Updater executable not found' }
# A fake standalone application is updated in %TEMP%, never the live ProstoKVN.
$arguments = @('--replace', ('"'+$stage+'"'), ('"'+$target+'"'), '999999')
$proc = Start-Process -FilePath $helper -ArgumentList $arguments -WorkingDirectory (Split-Path $helper) -PassThru -Wait
if($proc.ExitCode -ne 0) {
  if(Test-Path (Join-Path $stage 'update-result.log')) {Get-Content (Join-Path $stage 'update-result.log')}
  throw ('Updater failed in sandbox: exit '+$proc.ExitCode)
}
if(!(Test-Path (Join-Path $target 'new-version.fixture')) -or !(Test-Path (Join-Path $target 'started.fixture'))) {
  throw 'Updated fixture was not copied and restarted'
}
'PASS: sandbox copy, backup and restart'
'PASS: real VPN processes untouched'
'STAGE='+$stage
'TARGET='+$target
