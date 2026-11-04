# ProstoKVN

**ProstoKVN** — VPN-клиент для Windows 10/11 (x64) с раздельной маршрутизацией приложений, сайтов и IP-адресов. Выбирайте, что направлять через VPN, а что оставлять на прямом подключении. Поддерживаются сетевые ядра **Mihomo, sing-box и Xray**.

**Актуальная версия: [0.30.2](https://github.com/GRESHNIK2019/ProstoKVN/releases/tag/v0.30.2)** · [Все релизы](https://github.com/GRESHNIK2019/ProstoKVN/releases) · [Список изменений](release/CHANGELOG-v0.30.2-reliability.md)

## Скачать ProstoKVN

| Вариант | Кому подходит | Размер |
|---|---|---:|
| **[Скачать установщик Windows (.exe)](https://github.com/GRESHNIK2019/ProstoKVN/releases/download/v0.30.2/ProstoKVN-Setup-0.30.2-UNSIGNED.exe)** | Первая установка, ручное обновление | 91,5 МБ |
| **[Скачать автономный ZIP](https://github.com/GRESHNIK2019/ProstoKVN/releases/download/v0.30.2/ProstoKVN-v0.30.2-win-x64.zip)** | Портативная версия, ручная замена файлов | 128,3 МБ |

**Начиная с 0.30.2 устанавливать .NET отдельно не требуется:** .NET 10 Desktop Runtime уже включён в сборку.

Контрольные суммы: [SHA-256 установщика](https://github.com/GRESHNIK2019/ProstoKVN/releases/download/v0.30.2/ProstoKVN-Setup-0.30.2-UNSIGNED.exe.sha256) · [SHA-256 ZIP](https://github.com/GRESHNIK2019/ProstoKVN/releases/download/v0.30.2/ProstoKVN-v0.30.2-win-x64.zip.sha256)

> **Обратите внимание:** текущие файлы не подписаны доверенным сертификатом Authenticode. SmartScreen может показать предупреждение о неизвестном издателе. Проверяйте контрольные суммы и не отключайте защиту Windows.

## Возможности

- **Выборочная маршрутизация:** правила для приложений, доменов и IP-адресов; направление трафика через VPN или напрямую.
- **TUN и системный прокси:** режим зависит от выбранной конфигурации; для TUN могут потребоваться права администратора.
- **Несколько подписок одновременно:** источники 3x-ui и других провайдеров, объединённый список серверов с названиями подписок.
- **Автоопределение форматов:** Base64 / V2Ray-ссылки, Mihomo / Clash YAML, sing-box JSON и Xray JSON.
- **Сетевые ядра:** Mihomo, sing-box и Xray; поддерживаемые протоколы включают VLESS / REALITY и Hysteria2 (в зависимости от конфигурации сервера и ядра).
- **Обновление из программы:** GitHub Releases, проверка SHA-256, резервная копия заменяемых файлов и перезапуск.
- **Диагностика соединения:** контроль состояния VPN и попытки восстановления при повторных сбоях.

## Что нового в 0.30.2

- Автономная сборка со встроенным .NET 10 Desktop Runtime.
- Компактный обновлятор **Native AOT** без второй копии среды .NET; ZIP уменьшен примерно на 21% по сравнению с ранней сборкой 0.30.2.
- Восстановление сохранённых системных настроек прокси при следующем запуске после сбоя, если их не изменило другое приложение.
- Атомарное сохранение настроек и маршрутов с резервными копиями.
- Защита от пересекающихся операций подключения, отключения и переключения VPN.
- Параллельное обновление до трёх подписок с ограничением ответа до 8 МБ.
- Очистка старых временных файлов обновления.

Подробности: [CHANGELOG 0.30.2](release/CHANGELOG-v0.30.2-reliability.md).

## Установка и обновление

**Новая установка:** скачайте установщик выше и следуйте мастеру. Программа устанавливается в `%LOCALAPPDATA%\Programs\ProstoKVN`.

**Портативный запуск:** распакуйте ZIP в отдельную папку и запустите `ProstoKVN.exe`. Не запускайте одновременно две копии клиента.

**Обновление работающей версии:** откройте **Настройки → Обновления приложения → Проверить обновления**. Программа проверит последнюю стабильную версию на GitHub, скачает архив после подтверждения, проверит SHA-256 и запустит отдельный обновлятор. Во время установки VPN-соединение временно прервётся.

**Если старый ProstoKVN не запускается из-за отсутствующего .NET:** обновление из программы недоступно — один раз установите новую автономную версию вручную.

**Важно:** установщик не переносит автоматически старую портативную папку и не удаляет её. Пользовательские настройки, правила и зашифрованные ссылки подписок хранятся отдельно от файлов программы:

```text
%LOCALAPPDATA%\ProstoKVN
```

### Проверка скачанного файла

В PowerShell выполните:

```powershell
Get-FileHash .\ProstoKVN-Setup-0.30.2-UNSIGNED.exe -Algorithm SHA256
```

SHA-256 установщика 0.30.2:

```text
AD206D754E867DAE8F25F89E7D6FED386C43651B009F199C27EB02295DFEBC57
```

SHA-256 ZIP 0.30.2:

```text
57E0C8DA50914A980841DC356DEB3CF8376BE6F4412455F3135FF5DE96F31A75
```

## Сборка из исходного кода

Для разработки необходимы **Windows x64** и **.NET 10 SDK**. Собрать клиент и тесты:

```powershell
dotnet build .\ProstoKVN\ProstoKVN.csproj -c Release
dotnet build .\Updater\ProstoKVN.Updater.csproj -c Release
dotnet build .\SmokeTest\SmokeTest.csproj -c Release
```

Для полного дистрибутива дополнительно нужны оригинальные файлы поддерживаемых сетевых ядер и их лицензии. Скрипты упаковки — в `release/Build-Release.ps1` и `release/Build-Installer.ps1` (Inno Setup 6). Публикация подписанного релиза требует сертификат Code Signing.

## Безопасность и лицензии

Подписки и правила хранятся локально. **Не публикуйте свои ссылки подписок, токены и файлы настроек.** Данные сторонних подписок обрабатываются как конфигурация серверов: чужие правила DNS и маршрутизации не применяются автоматически.

[Лицензия проекта](LICENSE). Библиотеки и компоненты Mihomo, sing-box, Xray и YamlDotNet распространяются на условиях собственных лицензий, включённых в бинарный дистрибутив.

---

## English

**ProstoKVN** is a Windows 10/11 x64 VPN client with per-app, per-domain and IP-based split routing. It supports multiple subscriptions (Base64 / V2Ray, Mihomo YAML, sing-box JSON and Xray JSON), TUN, system proxy, and the Mihomo, sing-box and Xray cores.

**[Download v0.30.2](https://github.com/GRESHNIK2019/ProstoKVN/releases/tag/v0.30.2)** — choose the Windows installer for a new installation or the self-contained ZIP for portable use. **.NET 10 Desktop Runtime is included.**

Existing clients with an operational updater can select **Settings → Application updates → Check for updates**. Clients that cannot start due to a missing .NET runtime require a one-time manual install. Settings remain under `%LOCALAPPDATA%\ProstoKVN`.

**Unsigned binaries:** Windows SmartScreen may warn about an unknown publisher. Do not disable Windows security protections; verify the published SHA-256 checksums.
