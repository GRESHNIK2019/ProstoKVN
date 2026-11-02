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
