# v2rayN

[English](README.md) | **Русский** | [中文](README-CN.md)

### GUI-клиент для Windows, Linux и macOS. Поддерживает [Xray](https://github.com/XTLS/Xray-core), [sing-box](https://github.com/SagerNet/sing-box) и [другие ядра](https://github.com/2dust/v2rayN/wiki/List-of-supported-cores)

[![CodeFactor](https://www.codefactor.io/repository/github/h1w/v2rayn-ru/badge)](https://www.codefactor.io/repository/github/h1w/v2rayn-ru)
[![Release](https://img.shields.io/github/v/release/h1w/v2rayN-RU?logo=github&label=Release)](https://github.com/h1w/v2rayN-RU/releases)
[![Downloads](https://img.shields.io/github/downloads/h1w/v2rayN-RU/latest/total?logo=github&label=Downloads)](https://github.com/h1w/v2rayN-RU/releases)
[![Telegram](https://img.shields.io/badge/Telegram-Chat-26A5E4?logo=telegram)](https://t.me/v2rayn)
 
[![Windows](https://img.shields.io/badge/Windows-supported-0078D6?logo=windows)](https://github.com/h1w/v2rayN-RU) 
[![Linux](https://img.shields.io/badge/Linux-supported-FCC624?logo=linux&logoColor=000)](https://github.com/h1w/v2rayN-RU) 
[![macOS](https://img.shields.io/badge/macOS-supported-000000?logo=apple)](https://github.com/h1w/v2rayN-RU) 
[![GPG Signed](https://img.shields.io/badge/GPG-signed-4B32C3?logo=gnuprivacyguard)](https://github.com/h1w/v2rayN-RU)


---

## Об этом форке

**v2rayN-RU** — форк [v2rayN](https://github.com/2dust/v2rayN) с дополнительными возможностями:

- **Поддержка HWID клиента** — при обновлении подписок клиент отправляет Happ-совместимые HWID-заголовки (`x-hwid`, `x-device-os`, `x-ver-os`, `x-device-locale`), совместимые с Remnawave Panel v2.9.0+. Это позволяет использовать подписки, требующие авторизации по аппаратному идентификатору устройства.
- **Полная поддержка конфигураций `.json`** — импорт и запуск произвольных пользовательских `.json`-конфигураций ядра как есть.

---

## Загрузка

Скачать последнюю версию можно здесь:

[https://github.com/h1w/v2rayN-RU/releases](https://github.com/h1w/v2rayN-RU/releases)


> [!TIP]
> v2rayN — версия для ПК. Мобильную версию смотрите в v2rayNG
>
> https://github.com/2dust/v2rayNG

Для локальной переносимой Windows x64 сборки установите .NET 10 SDK и выполните
`powershell -NoProfile -ExecutionPolicy Bypass -File .\build-windows.ps1` из корня
репозитория. Скрипт публикует приложение и AmazTool, копирует ядра из локальной
папки `bin` в `publish\win-x64`. Ядра Xray и sing-box должны уже находиться в `bin`.
Пользовательские настройки не копируются и не удаляются.

---

## Документация

Руководства по использованию и настройке смотрите в Wiki.

[https://github.com/2dust/v2rayN/wiki](https://github.com/2dust/v2rayN/wiki)

В **Rule Settings** галочка в первом столбце и переключатель в редакторе правила
отражают одно состояние: редактор открывается с текущим значением галочки,
а подтверждённое изменение переключателя обновляет список. Отмена редактирования
не меняет состояние правила. Для сохранения изменений нажмите кнопку сохранения
в окне **Rule Settings**.
При обновлении JSON-подписки сохранённый порядок JSON-правил относительно локальных
и их состояние включения переносятся на соответствующий обновлённый профиль.
Состояние сохраняется и при временно выключенной настройке редактирования JSON-правил.

При включённых TUN и **Legacy TUN Protect** для управляемого Custom Xray
вспомогательный sing-box создаётся автоматически, даже если **Socks port** пуст.
Он принимает TUN и подключения к локальному mixed-порту приложения, а основной
Xray получает отдельный внутренний SOCKS-порт на loopback. Этот порт выбирается
при запуске и не сохраняется в подписку: сброс поля при обновлении подписки
не отключает Legacy TUN. Общая маршрутизация остаётся в основном ядре;
вспомогательное ядро лишь передаёт пользовательский трафик. Для остальных
неуправляемых Custom-конфигураций сохраняется прежняя ручная настройка порта.

Для управляемых Custom-конфигураций в TUN транспортные адреса поддерживаемых
outbound-серверов защищаются точными парами «адрес + порт»: вспомогательный
sing-box направляет их напрямую даже при недоступном определении процесса.
Явные цепочки `dialerProxy`, `proxySettings` и `detour` учитываются; весь порт
или весь IP целиком не исключается. Другие приложения, обращающиеся к той же
точной паре, также используют прямой выход. Для доменов используется обратное
сопоставление DNS-ответов, наблюдаемых sing-box: старый внешний DNS-кеш и
сторонний зашифрованный DNS не гарантируют такое сопоставление. Для динамических
SRV/TXT-адресов и неподдерживаемых схем остаётся защита по процессу.

Повторяющиеся сообщения `connection download closed: close tcp …: endpoint not connected`
в обычном журнале сворачиваются в информационный счётчик не чаще раза в 30 секунд.
Исходные строки сохраняются в диагностическом журнале `guiLogs`. Непустой фильтр
сообщений (например, `endpoint not connected`) показывает новые исходные строки;
уже свёрнутые строки в окно не возвращаются. При отключённом диагностическом
логировании сворачивание отключается, чтобы не терять сообщения. Ошибки подключения,
TLS, тайм-ауты и другие ошибки не скрываются.

---

## Поддерживаемые платформы

| Платформа | x64 | x86 | arm64 | riscv64 | loong64 |
| --- | --- | --- | --- | --- | --- |
| Windows | ✅ | ✅ | ✅ | - | - |
| Linux | ✅ | - | ✅ | ✅ | ✅ |
| macOS | ✅ | - | ✅ | - | - |

---

## Проверка подписи GPG

Файлы релизов подписаны с помощью GPG для проверки подлинности и целостности — это помогает предотвратить подмену со стороны зеркал, интернет-провайдера или CDN.

### Отпечаток ключа

```text
ECF0 C3FB E838 19F6 6D5D
0989 C946 B144 9B53 7603
```

---

## Сообщество

Telegram-группа:

[https://t.me/v2rayN](https://t.me/v2rayN)

Telegram-канал:

[https://t.me/github_2dust](https://t.me/github_2dust)
