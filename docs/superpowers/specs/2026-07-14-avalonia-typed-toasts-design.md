# Дизайн: типизированные тост-уведомления для Avalonia (Desktop)

**Дата:** 2026-07-14
**Ветка:** `feature/avalonia-typed-toasts`
**Фронтенд:** только `v2rayN.Desktop` (Avalonia)
**Подход:** A — вся логика изолирована в файлах форка, код оригинала (`ServiceLib`, `v2rayN` WPF) не изменяется.

## Цель

Сделать уведомления в Avalonia-версии типизированными и «красивыми»: успех — зелёный,
ошибка — красный, предупреждение — жёлтый, инфо — нейтральный. Отдельно показывать явный
зелёный тост «Подключено · <сервер>» при старте/смене сервера и info-тост о доступном
обновлении. Всё — на встроенном `WindowNotificationManager` + теме Semi.Avalonia.

## Мотивация форка

Проект — форк `2dust/v2rayN`. Изменения должны жить в отдельных/новых файлах Avalonia-проекта,
чтобы обновления апстрима мёржились без конфликтов. Поэтому **никакие файлы `ServiceLib` и
WPF-проекта не трогаются**. Единственная правка в существующем файле — в
`v2rayN.Desktop/Views/MainWindow.axaml.cs`, который и так принадлежит форку по смыслу изменений.

## Текущее состояние (как есть)

- Единая точка уведомлений: `NoticeManager.Instance.Enqueue(msg)` публикует
  `AppEvents.SendSnackMsgRequested` (только строка, без типа).
- WPF рисует нижний MaterialDesign `Snackbar`.
- Avalonia рисует тост в правом верхнем углу через `WindowNotificationManager`
  (`MainWindow.axaml.cs`, метод `DelegateSnackMsg`), **всегда** с типом
  `NotificationType.Information`.
- Подключение: `CoreManager.LoadCore()` при успехе вызывает `UpdateHandler(true, summary)`,
  что приводит к `Enqueue(summary)`. Реактивное свойство `StatusBarViewModel.RunningServerDisplay`
  обновляется именем запущенного сервера в `RefreshServersBiz()`.
- Отключение: `CoreManager.CoreStop()` не шлёт уведомлений; явного «дисконнекта» в UX v2rayN нет.

## Архитектура

Вся работа — только в `v2rayN.Desktop`. Три новых файла и точечная правка одного нашего файла.

| Файл | Роль | Статус |
|---|---|---|
| `v2rayN.Desktop/Common/ToastType.cs` | enum `Success/Error/Warning/Info` | новый |
| `v2rayN.Desktop/Common/ToastClassifier.cs` | строка сообщения → `ToastType` | новый |
| `v2rayN.Desktop/Manager/ToastService.cs` | обёртка над `WindowNotificationManager` | новый |
| `v2rayN.Desktop/Views/MainWindow.axaml.cs` | подключение `ToastService` вместо хардкода `Information` + подписки | правка |

### Поток данных

```
NoticeManager.Enqueue(msg)  ──►  AppEvents.SendSnackMsgRequested  ──►  MainWindow.DelegateSnackMsg(msg)
   (общий код, НЕ трогаем)              (общий код, НЕ трогаем)              │  (наш файл)
                                                                            ▼
                                                        ToastClassifier.Classify(msg) → ToastType
                                                                            ▼
                                                        ToastService.Show(msg, type)  ──► тост
```

Перехват — на последнем шаге, который и так принадлежит Avalonia-проекту. Ничего в `ServiceLib`
не меняется.

## Компоненты

### 1. `ToastType` (enum)

```
Success, Error, Warning, Info
```

Маппинг на `Avalonia.Controls.Notifications.NotificationType`:
`Success → Success`, `Error → Error`, `Warning → Warning`, `Info → Information`.

### 2. `ToastClassifier` — строка → тип

**Идея:** культуронезависимая классификация по **именам ключей** ресурса `ResUI`, а не по
локализованному тексту. При первом обращении один раз строим словарь
`resolved_value → ToastType`, пробегая рефлексией по строковым свойствам `ResUI`:

- имя ключа содержит `Success` → `Success`
- имя ключа содержит `Fail` или `Error` → `Error`
- имя ключа содержит `Please`, `Invalid`, `Tip`, `Warn` → `Warning`
- иначе ключ не попадает в словарь

Классификация входящего сообщения:

1. Прямое совпадение с построенным словарём (после `Trim`) → соответствующий тип.
2. Иначе эвристика по подстроке резолвнутых значений известных success/error-ключей
   (для форматированных сообщений вроде `string.Format(ResUI.StartService, ...)`).
3. Иначе → `Info` (дефолт).

Свойства:
- Чистая функция (без побочных эффектов), результат кэшируется на уровне словаря.
- Построение словаря обёрнуто в try/catch; при сбое рефлексии — весь трафик классифицируется
  как `Info`, тосты продолжают работать.
- **Самоподдержка при апдейтах апстрима:** новые строки `ResUI` подхватываются автоматически по
  имени ключа — правок форка при обновлении оригинала не требуется.

### 3. `ToastService` — показ тоста

Обёртка над `WindowNotificationManager` (сейчас создаётся прямо в `MainWindow`; переносим
создание/владение в `ToastService`, конфигурация та же: `MaxItems = 3`,
`Position = NotificationPosition.TopRight`).

API:

```
void Show(string message, ToastType type);
void ShowConnected(string serverSummary);   // Success, текст "Подключено · <сервер>"
void ShowUpdateAvailable();                  // Info, текст о доступном обновлении
```

- `null`/пустой менеджер (окно ещё не готово) → тихий no-op (`?.Show`), как сейчас.
- Пустое сообщение → не показываем (дополнительно к отсечке в `NoticeManager.Enqueue`).
- Заголовок тоста — по типу (например, локализованные «Готово / Ошибка / Внимание») либо `null`.

### 4. Правки в `MainWindow.axaml.cs` (наш файл)

- `DelegateSnackMsg(content)`: вместо
  `_manager?.Show(new Notification(null, content, NotificationType.Information))` →
  `_toast.Show(content, ToastClassifier.Classify(content))`.
- Подписка на реактивное `ViewModel.RunningServerDisplay` (через `WhenAnyValue` в блоке
  `WhenActivated`): на непустое изменение имени сервера → `_toast.ShowConnected(server)`.
  Первичная установка при инициализации не должна порождать ложный тост (пропускаем первое
  значение / показываем только на реальных переходах).
- Подписка на `AppEvents.HasUpdateNotified` → `_toast.ShowUpdateAvailable()`.

## Обработка ошибок и краевые случаи

- Окно/менеджер не готов → no-op.
- Пустая строка → не показываем.
- Сбой рефлексии `ResUI` → фолбэк на `Info` для всего.
- Классификатор устойчив к регистру и пробелам (`Trim`, сравнение без учёта регистра).
- «Отключено» намеренно не реализуется: в v2rayN нет явного дисконнекта; показываем только
  «Подключено» при старте и смене сервера.

## Тестирование

**Решение по юнит-тестам (важно для Подхода A):** отдельный тест-проект добавлять НЕ будем — это
потребовало бы правки `v2rayN.sln` (файл апстрима → риск конфликтов при слиянии). Существующий
`ServiceLib.Tests` не может (и не должен) ссылаться на Avalonia-проект `v2rayN.Desktop`, где живёт
классификатор. Поэтому:

- **Основная проверка — визуальная**, запуском Avalonia-версии на Linux (Arch):
  - подключение → зелёный «Подключено · <сервер>»;
  - смена сервера → зелёный «Подключено» с новым именем;
  - ошибка старта ядра → красный тост;
  - проверка обновлений / доступна новая версия → info-тост;
  - сохранение настроек, невалидный ввод → success / warning.
- **Ручной self-check логики**: `ToastClassifier` пишется как чистая, самодостаточная функция;
  корректность маппинга ключей проверяется по таблице соответствий в этом документе и обзором кода.
- Если в будущем захотим полноценные юнит-тесты — это отдельная задача с решением по размещению
  тест-проекта (вне рамок текущей фичи, чтобы не трогать `v2rayN.sln`).

## Вне рамок (YAGNI)

- WPF-фронтенд не трогаем.
- Кастомный тост-контрол (свои анимации/прогресс-бар) — не делаем, используем нативные тосты Semi.
- Отдельный тост «Отключено».
- Изменение канала `SendSnackMsgRequested` или `NoticeManager` в общем коде.

## Влияние на слияния с апстримом

- Новые файлы (`ToastType`, `ToastClassifier`, `ToastService`) — нулевой риск конфликтов.
- Единственная правка существующего файла — `v2rayN.Desktop/Views/MainWindow.axaml.cs`
  (наш метод `DelegateSnackMsg` + подписки в `WhenActivated`). Конфликт возможен только если апстрим
  переписывает ровно этот участок; разрешается тривиально.
