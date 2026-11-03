# ProstoKVN

**Русский:** ProstoKVN — VPN-клиент для Windows с раздельной маршрутизацией приложений, сайтов и IP-адресов. Поддерживает TUN и ядра Mihomo, sing-box, Xray. Можно направлять выбранные приложения через VPN, а остальной трафик — напрямую.

**English:** ProstoKVN is a Windows VPN client with split routing by app, domain and IP. Supports TUN and the Mihomo, sing-box and Xray networking cores. Selected traffic can use VPN while other connections remain direct.

## Скачать / Download

[GitHub Releases — последняя версия / latest release](https://github.com/GRESHNIK2019/ProstoKVN/releases/latest)

Начиная с версии **0.30.0**, можно добавить **несколько подписок одновременно**. Формат определяется автоматически по содержимому: **Base64 / V2Ray**, **Mihomo / Clash YAML**, **sing-box JSON**, **Xray JSON**. Серверы отображаются в общем списке с названиями источников. Существующие подписки 3x-ui сохраняются при обновлении.

Starting in **0.30.0**, you can use **multiple subscriptions** simultaneously. Supported auto-detected formats are **Base64 / V2Ray**, **Mihomo / Clash YAML**, **sing-box JSON**, and **Xray JSON**. Nodes are grouped by subscription name in the common selector. Existing 3x-ui subscriptions are preserved.

The 0.30.0 release also fixes an unwanted bright background behind subscription cards in the dark theme.

## Обновления / Updates

Откройте **Настройки → Обновления → Проверить обновления**. Стабильные выпуски публикуются в GitHub Releases и содержат:

- `ProstoKVN-vMAJOR.MINOR.PATCH-win-x64.zip`
- `ProstoKVN-vMAJOR.MINOR.PATCH-win-x64.zip.sha256`

Обновление скачивается после подтверждения, проверяется по SHA-256, затем отдельный обновлятор заменяет файлы и перезапускает приложение. Пользовательские настройки и зашифрованные ссылки хранятся в `%LOCALAPPDATA%\ProstoKVN` и не входят в архив.

Updates are user-approved, verified against SHA-256, installed by a separate updater and followed by an application restart. User settings and encrypted subscription URLs remain outside the installation directory.

**Безопасность / Security:** сборка пока не имеет подписи Authenticode, поэтому SmartScreen может показать предупреждение о неизвестном издателе. Не отключайте защиту Windows. Проверьте SHA-256.

This release is currently **unsigned**. Windows SmartScreen may warn about an unknown publisher. Do not disable security software; verify the published SHA-256 checksum.

## Сборка / Build

Windows 10/11 x64, .NET 10 SDK and .NET Desktop Runtime 10:

```powershell
dotnet build .\ProstoKVN\ProstoKVN.csproj -c Release
dotnet build .\Updater\ProstoKVN.Updater.csproj -c Release
dotnet build .\SmokeTest\SmokeTest.csproj -c Release
```

`release/Build-Release.ps1` prepares a Windows x64 ZIP and SHA-256 checksum. Inno Setup configuration: `release/ProstoKVN.iss`. A publicly trusted installer requires a valid publisher code-signing certificate.

## Лицензии / Licenses

The project license is in [LICENSE](LICENSE). The packaged Mihomo, sing-box, Xray and YamlDotNet components retain their own licenses in the distribution. Subscription files are parsed as data; foreign DNS and routing settings are not applied to the user's system.
