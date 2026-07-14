# Типизированные тост-уведомления для Avalonia — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Сделать уведомления в Avalonia-версии v2rayN типизированными (успех/ошибка/предупреждение/инфо) с явным зелёным тостом «Подключено · <сервер>» и info-тостом об обновлении, не затрагивая код апстрима.

**Architecture:** Подход A (изоляция в форке). Три новых файла в `v2rayN.Desktop` (`ToastType`, `ToastClassifier`, `ToastService`) плюс точечная правка нашего `MainWindow.axaml.cs`. Сообщения, идущие через существующее событие `AppEvents.SendSnackMsgRequested`, классифицируются по именам ключей ресурса `ResUI` и показываются нативным `WindowNotificationManager` (тема Semi.Avalonia). «Подключено» ловится через реактивное свойство `StatusBarViewModel.RunningServerDisplay`, обновление — через событие `AppEvents.HasUpdateNotified`.

**Tech Stack:** C# / .NET 10, Avalonia, ReactiveUI, Semi.Avalonia, `Avalonia.Controls.Notifications.WindowNotificationManager`.

## Global Constraints

- **Только `v2rayN.Desktop`.** Ни один файл в `ServiceLib`, `v2rayN` (WPF), `v2rayN.sln`, `*.resx`, `*.props` не изменяется (Подход A — чистые слияния с апстримом).
- **TFM:** `net10.0` (из `Directory.Build.props`).
- **Namespaces:** новые файлы — `v2rayN.Desktop.Common` (Common/) и `v2rayN.Desktop.Manager` (Manager/), как в существующих файлах этих папок.
- **Без хардкода локализуемого текста, кроме «Подключено»:** строки берём из `ResUI`; исключение — «Подключено» (в ресурсах отсутствует, добавлять в апстрим-`resx` нельзя), поэтому это единственная захардкоженная русская строка, вынесенная в `const` в `ToastService`.
- **Юнит-тесты вне рамок** (добавление тест-проекта потребовало бы правки `v2rayN.sln` — апстрим-файл). Верификация каждой задачи — успешная сборка; финальная задача — ручная проверка в запущенном приложении.

## Prerequisites

`dotnet` присутствует, но **SDK .NET 10 не установлен** (`dotnet --list-sdks` пуст / ошибка «No .NET SDKs were found»). Без SDK сборка невозможна.

- [ ] **P1:** Установить .NET 10 SDK (на Arch — из репозитория/AUR пакет с .NET 10, напр. `dotnet-sdk` соответствующей версии или бинарник с https://dotnet.microsoft.com/download/dotnet/10.0).
- [ ] **P2:** Проверить: `dotnet --list-sdks` — в выводе есть строка `10.x.xxx`.
- [ ] **P3:** Базовая сборка проходит:

Run: `cd /home/bpqvg/dev/v2rayN-RU/v2rayN && dotnet build v2rayN.Desktop/v2rayN.Desktop.csproj -c Debug`
Expected: `Build succeeded` (0 ошибок). Это baseline — фиксирует, что окружение готово до наших изменений.

---

### Task 1: `ToastType` + `ToastClassifier`

**Files:**
- Create: `v2rayN/v2rayN.Desktop/Common/ToastType.cs`
- Create: `v2rayN/v2rayN.Desktop/Common/ToastClassifier.cs`

**Interfaces:**
- Produces:
  - `enum ToastType { Info, Success, Warning, Error }` в `v2rayN.Desktop.Common`
  - `static class ToastClassifier` с методом `static ToastType Classify(string? message)` в `v2rayN.Desktop.Common`

**Как это работает:** при первом вызове рефлексией строится словарь `resolved_ResUI_value → ToastType` по имени ключа (Success / Fail|Error / Please|Invalid|Warn|Tip). `Classify` делает exact-match входящей строки (после `Trim`) по словарю; иначе → `Info`. (Замечание: подстрочная эвристика из спеки намеренно опущена — она давала бы ложные срабатывания; тост-сообщения на практике являются точными значениями `ResUI` вроде `OperationSuccess`/`OperationFailed`, а форматированные строки безопасно попадают в `Info`.)

- [ ] **Step 1: Создать `ToastType.cs`**

```csharp
namespace v2rayN.Desktop.Common;

public enum ToastType
{
    Info,
    Success,
    Warning,
    Error
}
```

- [ ] **Step 2: Создать `ToastClassifier.cs`**

```csharp
using System.Reflection;

namespace v2rayN.Desktop.Common;

public static class ToastClassifier
{
    private static readonly Lazy<Dictionary<string, ToastType>> _map = new(BuildMap);

    public static ToastType Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return ToastType.Info;
        }

        return _map.Value.TryGetValue(message.Trim(), out var type) ? type : ToastType.Info;
    }

    private static ToastType CategoryFromKeyName(string keyName)
    {
        if (keyName.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            return ToastType.Success;
        }
        if (keyName.Contains("Fail", StringComparison.OrdinalIgnoreCase)
            || keyName.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            return ToastType.Error;
        }
        if (keyName.Contains("Please", StringComparison.OrdinalIgnoreCase)
            || keyName.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
            || keyName.Contains("Warn", StringComparison.OrdinalIgnoreCase)
            || keyName.Contains("Tip", StringComparison.OrdinalIgnoreCase))
        {
            return ToastType.Warning;
        }
        return ToastType.Info;
    }

    private static Dictionary<string, ToastType> BuildMap()
    {
        var map = new Dictionary<string, ToastType>(StringComparer.Ordinal);
        try
        {
            var props = typeof(ResUI).GetProperties(BindingFlags.Public | BindingFlags.Static);
            foreach (var p in props)
            {
                if (p.PropertyType != typeof(string))
                {
                    continue;
                }

                var category = CategoryFromKeyName(p.Name);
                if (category == ToastType.Info)
                {
                    continue;
                }

                if (p.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    map[value.Trim()] = category;
                }
            }
        }
        catch
        {
            // Сбой рефлексии → пустой словарь → всё классифицируется как Info.
        }
        return map;
    }
}
```

Примечание: `ResUI` доступен через global using `ServiceLib.Resx` (файл `v2rayN.Desktop/GlobalUsings.cs`).

- [ ] **Step 3: Сборка**

Run: `cd /home/bpqvg/dev/v2rayN-RU/v2rayN && dotnet build v2rayN.Desktop/v2rayN.Desktop.csproj -c Debug`
Expected: `Build succeeded`, 0 ошибок.

- [ ] **Step 4: Commit**

```bash
cd /home/bpqvg/dev/v2rayN-RU
git add v2rayN/v2rayN.Desktop/Common/ToastType.cs v2rayN/v2rayN.Desktop/Common/ToastClassifier.cs
git commit -m "feat(desktop): тип тоста и классификатор по ключам ResUI"
```

---

### Task 2: `ToastService`

**Files:**
- Create: `v2rayN/v2rayN.Desktop/Manager/ToastService.cs`

**Interfaces:**
- Consumes: `ToastType` (Task 1).
- Produces: `class ToastService` в `v2rayN.Desktop.Manager` с конструктором `ToastService(TopLevel? topLevel)` и методами:
  - `void Show(string? message, ToastType type)`
  - `void ShowConnected(string? serverSummary)`
  - `void ShowUpdateAvailable()`

- [ ] **Step 1: Создать `ToastService.cs`**

```csharp
using Avalonia.Controls.Notifications;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Manager;

public class ToastService
{
    private const string ConnectedTitle = "Подключено";

    private readonly WindowNotificationManager? _manager;

    public ToastService(TopLevel? topLevel)
    {
        if (topLevel != null)
        {
            _manager = new WindowNotificationManager(topLevel)
            {
                MaxItems = 3,
                Position = NotificationPosition.TopRight
            };
        }
    }

    public void Show(string? message, ToastType type)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _manager?.Show(new Notification(null, message, ToNotificationType(type)));
    }

    public void ShowConnected(string? serverSummary)
    {
        if (string.IsNullOrWhiteSpace(serverSummary))
        {
            return;
        }

        _manager?.Show(new Notification(ConnectedTitle, serverSummary, NotificationType.Success));
    }

    public void ShowUpdateAvailable()
    {
        _manager?.Show(new Notification(null, ResUI.menuNewUpdate, NotificationType.Information));
    }

    private static NotificationType ToNotificationType(ToastType type) => type switch
    {
        ToastType.Success => NotificationType.Success,
        ToastType.Warning => NotificationType.Warning,
        ToastType.Error => NotificationType.Error,
        _ => NotificationType.Information
    };
}
```

Примечание: `TopLevel` доступен через global using `Avalonia.Controls`; `ResUI` — через `ServiceLib.Resx`. `ResUI.menuNewUpdate` = «Доступно обновление» (проверено в `ResUI.ru.resx`).

- [ ] **Step 2: Сборка**

Run: `cd /home/bpqvg/dev/v2rayN-RU/v2rayN && dotnet build v2rayN.Desktop/v2rayN.Desktop.csproj -c Debug`
Expected: `Build succeeded`, 0 ошибок.

- [ ] **Step 3: Commit**

```bash
cd /home/bpqvg/dev/v2rayN-RU
git add v2rayN/v2rayN.Desktop/Manager/ToastService.cs
git commit -m "feat(desktop): ToastService над WindowNotificationManager"
```

---

### Task 3: Подключение к `MainWindow` + ручная проверка

**Files:**
- Modify: `v2rayN/v2rayN.Desktop/Views/MainWindow.axaml.cs`

**Interfaces:**
- Consumes: `ToastService` (Task 2), `ToastClassifier` (Task 1).

Изменения затрагивают 4 места в файле. Ниже — точные замены.

- [ ] **Step 1: Заменить поле `_manager` на `_toastService`**

Найти (строка ~14):

```csharp
    private readonly WindowNotificationManager? _manager;
```

Заменить на:

```csharp
    private readonly ToastService _toastService;
```

- [ ] **Step 2: Заменить создание менеджера в конструкторе**

Найти (строка ~24):

```csharp
        _manager = new WindowNotificationManager(TopLevel.GetTopLevel(this)) { MaxItems = 3, Position = NotificationPosition.TopRight };
```

Заменить на:

```csharp
        _toastService = new ToastService(TopLevel.GetTopLevel(this));
```

- [ ] **Step 3: Обновить `DelegateSnackMsg` (классификация)**

Найти (строки ~169-173):

```csharp
    private async Task DelegateSnackMsg(string content)
    {
        _manager?.Show(new Avalonia.Controls.Notifications.Notification(null, content, NotificationType.Information));
        await Task.CompletedTask;
    }
```

Заменить на:

```csharp
    private async Task DelegateSnackMsg(string content)
    {
        _toastService.Show(content, ToastClassifier.Classify(content));
        await Task.CompletedTask;
    }
```

- [ ] **Step 4: Добавить подписки на «Подключено» и «Обновление»**

В методе `WhenActivated`, сразу после блока подписки `AppEvents.SendSnackMsgRequested` (после строки `.DisposeWith(disposables);` этого блока, ~строка 121), добавить:

```csharp
            this.WhenAnyValue(v => v.ViewModel.StatusBarViewModel.RunningServerDisplay)
              .Skip(1)
              .DistinctUntilChanged()
              .Where(server => !string.IsNullOrWhiteSpace(server) && server != ResUI.CheckServerSettings)
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(server => _toastService.ShowConnected(server))
              .DisposeWith(disposables);

            AppEvents.HasUpdateNotified
              .AsObservable()
              .Where(hasUpdate => hasUpdate)
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(_ => _toastService.ShowUpdateAvailable())
              .DisposeWith(disposables);
```

Примечания:
- `Skip(1)` отбрасывает стартовое значение (сервер по умолчанию при запуске), чтобы не показывать ложный тост при старте; `DistinctUntilChanged` — против дублей; фильтр `!= ResUI.CheckServerSettings` отсекает состояние «сервер не выбран».
- `Skip`, `DistinctUntilChanged`, `Where` — из `System.Reactive.Linq` (global using присутствует). `WhenAnyValue` — из ReactiveUI (global using присутствует). Импорты `v2rayN.Desktop.Common` и `v2rayN.Desktop.Manager` в файле уже есть (строки 5-6).
- Строка `using Avalonia.Controls.Notifications;` (строка 2) больше не используется напрямую в этом файле; удалять не обязательно, но можно — сборка покажет предупреждение unused, не ошибку. Оставляем как есть, чтобы минимизировать diff.

- [ ] **Step 5: Сборка**

Run: `cd /home/bpqvg/dev/v2rayN-RU/v2rayN && dotnet build v2rayN.Desktop/v2rayN.Desktop.csproj -c Debug`
Expected: `Build succeeded`, 0 ошибок.

- [ ] **Step 6: Ручная проверка в приложении**

Run: `cd /home/bpqvg/dev/v2rayN-RU/v2rayN && dotnet run --project v2rayN.Desktop/v2rayN.Desktop.csproj -c Debug`

Проверить в правом верхнем углу:
- Подключение к серверу / смена активного сервера → **зелёный** тост с заголовком «Подключено» и именем сервера.
- Действие с ошибкой (напр. импорт неверной подписки) → **красный** тост.
- Сохранение настроек / «операция успешна» → **зелёный** тост.
- Невалидный ввод (напр. «выберите сервер») → **жёлтый** тост.
- «Проверить обновления» при наличии новой версии → **нейтральный** info-тост «Доступно обновление».
- Отсутствие ложного тоста «Подключено» сразу при запуске приложения.

Expected: типы/цвета соответствуют; ложных тостов при старте нет.

- [ ] **Step 7: Commit**

```bash
cd /home/bpqvg/dev/v2rayN-RU
git add v2rayN/v2rayN.Desktop/Views/MainWindow.axaml.cs
git commit -m "feat(desktop): типизированные тосты + Подключено/Обновление"
```

---

## Итоговое состояние

- 3 новых файла в `v2rayN.Desktop` + правка одного нашего `MainWindow.axaml.cs`.
- Все уведомления, идущие через `SendSnackMsgRequested`, окрашиваются по типу.
- Зелёный «Подключено · <сервер>» при старте/смене сервера.
- Info-тост «Доступно обновление».
- Код апстрима не изменён → слияния остаются чистыми.
