# Установщик ProstoKVN для Windows x64

## Готовые сборки 0.30.2
- `dist/ProstoKVN-Setup-0.30.2.exe` — обычный Inno Setup установщик; подпись издателя отсутствует.
- `dist/ProstoKVN-Setup-0.30.2-TEST-SIGNED.exe` — тестово подписанная сборка, **не доверенная Windows у других пользователей**.
- ZIP `dist/ProstoKVN-v0.30.2-win-x64.zip` не изменяется при подписании установщика — его SHA-256 остаётся прежним.

Устанавливается в `%LOCALAPPDATA%\Programs\ProstoKVN`; пользовательские настройки остаются в `%LOCALAPPDATA%\ProstoKVN`. Пакет содержит .NET 10 и VPN-ядра. Windows x64; два языка установщика — русский и английский.

## Сборка и подпись
Нужны Inno Setup 6, Windows SDK SignTool, .NET 10 SDK для компиляции клиента (готовый payload уже собран).

```powershell
# Только локальное тестирование: подпись самоподписанным сертификатом, Windows другим пользователям не доверяет.
powershell -NoProfile -ExecutionPolicy Bypass -File .\release\Build-Installer.ps1 -Version 0.30.2 -TestSignature

# Без подписи, отдельно помеченный выходной файл.
powershell -NoProfile -ExecutionPolicy Bypass -File .\release\Build-Installer.ps1 -Version 0.30.2 -Unsigned

# Публичная сборка, если установлен действующий доверенный сертификат Code Signing с закрытым ключом.
powershell -NoProfile -ExecutionPolicy Bypass -File .\release\Build-Installer.ps1 -Version 0.30.2 -CertificateThumbprint '<THUMBPRINT>'
```

Скрипт создаёт отдельную папку для установщика и не затрагивает исходный ZIP. Подписывает клиент, обновлятор и готовый установщик. Для публичной подписи использует SHA-256 и сервер штампов времени; проверяет итоговый статус Authenticode. Потребуется действительный сертификат от доверенного удостоверяющего центра или сервис облачного подписания (для последнего потребуются отдельные параметры провайдера, этот скрипт их пока не реализует).

Самоподписанный сертификат `ProstoKVN Development (TEST ONLY)` хранит закрытый ключ локально в хранилище CurrentUser/My. Скрипт **не** добавляет его в доверенные корневые центры. Такая подпись не убирает предупреждения SmartScreen и не приравнивается к удостоверенной подписи издателя.

**Важно:** установленный Inno Setup может требовать лицензию для коммерческого использования; проверь условия использования перед публичной коммерческой поставкой.

## Проверка
```powershell
Get-AuthenticodeSignature .\dist\ProstoKVN-Setup-0.30.2-TEST-SIGNED.exe
Get-FileHash .\dist\ProstoKVN-Setup-0.30.2-TEST-SIGNED.exe -Algorithm SHA256
```

Проверка установки и удаления на чистой Windows 10/11 x64 без установленного .NET — рекомендуемый финальный этап перед публикацией.