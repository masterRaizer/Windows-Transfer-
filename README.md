# Windows Transfer («Перенос»)

WPF-утилита на C# (.NET Framework 4.7.2). Работает с внешнего SSD (`F:\Windows Transfer`) и умеет:

- **переносить данные** между ПК: профили, папки программ, реестр, Wi-Fi, задания планировщика, список программ (robocopy);
- **снимать клон Windows** в WIM (DISM `/Capture-Image` с теневой копии работающей системы);
- **разворачивать клон или шаблон** (ISO/WIM/ESD) второй Windows рядом с основной: раздел, DISM `/Apply-Image`,
  проверка на вирусы (Sentinel + собственные правила + Защитник), сверка системных файлов `sfc /offbootdir`,
  драйверы этого ПК (`dism /Add-Driver`), загрузчик (`bcdboot`, основная Windows остаётся первой).

Инструкция для пользователя — `ПРОЧТИ.txt`.

## Сборка

На рабочей машине есть только старый MSBuild из .NET Framework (компилятор C# 5):

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe src\Transfer.csproj /p:Configuration=Release /p:LangVersion=Default
```

Готовый `src\bin\Transfer.exe` кладётся на диск переноса как `F:\Windows Transfer\Перенос.exe`.
Антивирус Sentinel (сейчас — релиз 1.3.11 с полной базой угроз) встраивается в него автоматически из `src\sentinel\sentinel.zip.part*`
(exe получается ≈ 103 МБ).

### Обновить встроенный Sentinel

`src\sentinel\sentinel.zip` — Sentinel для Windows x64 (автономный, .NET на компьютере не нужен):
`Sentinel.Cli.exe`, `rules\`, `data\`, `allowed_exclusions.json`, `THIRD_PARTY_NOTICES.md`, `sentinel-version.txt`.
Берётся из **официального релиза** Antivirus-tool (переносная версия `Sentinel-vX-portable-win-x64.zip`): в релизном
`Sentinel.Cli.exe` собрана большая база хешей угроз из открытых источников, которую из исходников без ключей не получить.
Обновляет скрипт (нужны bash, curl, python3; .NET SDK не нужен):

```
scripts/update-sentinel.sh            # последний релиз
scripts/update-sentinel.sh 1.3.11     # конкретный релиз
scripts/update-sentinel.sh --source main   # сборка из исходников (нужен .NET SDK 8; только стартовая база угроз)
```

GitHub не принимает файлы больше 100 МБ, поэтому архив больше 90 МБ скрипт режет на части `sentinel.zip.part1`, `.part2`, …;
MSBuild встраивает все части, а программа склеивает их перед распаковкой. После обновления — обычная сборка MSBuild.
Если архива нет, программа всё равно собирается и ищет установленный Sentinel.

### Ограничения кода

- Только **C# 5**: без `$""`, `?.`, `nameof`, `out var`, expression-bodied членов, инициализаторов автосвойств,
  кортежей, локальных функций и pattern matching. Необязательные параметры и лямбды можно.
- Никаких NuGet-пакетов: только сборки .NET Framework (`System.Management`, `System.Web.Extensions` для JSON и т. п.).
- Исходники — UTF-8 **с BOM** (старый `csc` без BOM может прочитать кириллицу в системной кодировке).

Проверить совместимость с C# 5 без Windows можно компиляцией всех `.cs` с `LangVersion=5`
(XAML-поля окна заменяются заглушками) — так проверялась каждая правка в этом репозитории.

## Архитектура (`src/`)

| Файл | Что внутри |
|---|---|
| `App.xaml(.cs)` | Запуск: копия exe в `%LOCALAPPDATA%\Perenos` (чтобы отвал USB не убил программу), `--root`, single-instance mutex `Perenos.SingleInstance`, уборка после сбоя (`Maintenance`: кусты реестра `XFER*`, теневая копия, scratch DISM) |
| `MainWindow.xaml(.cs)` | Интерфейс: главный экран, выбор, экран прогресса (таймер `Tick` раз в 0,5 с), итог. Кнопки «Пауза», «Отменить», «Пропустить проверку» |
| `Engine.cs` | `Runner` — robocopy с прогрессом/скоростью/ETA, ожидание диска, пауза/отмена; флаги `SkipLabel`/`SkipRequested` |
| `Flows.cs` | `Flow` — база сценария (этапы, `Activity`, `StageFraction`/`StageStat`/`StageEta`, отслеживание диска переноса по `data\xfer.id`), `StageClock` — оставшееся время этапа; `BackupFlow`, `RestoreFlow` |
| `Clone.cs` | `CloneFlowBase` (DISM с процентами, коды ошибок, повтор при обрыве диска, `AddDrivers`, проверки), `CloneCaptureFlow`, `CloneApplyFlow`, `CloneFinishFlow` |
| `Images.cs` | Список Windows/клонов/шаблонов, `TemplateImportFlow` (копирование шаблона с докачкой) |
| `Security.cs` | Собственная проверка: Защитник (MpCmdRun), исключения Защитника, автозапуск, задачи, службы, брандмауэр, майнеры в файлах |
| `Sentinel.cs` | `SentinelScan` — распаковка встроенного Sentinel, запуск и разбор отчётов; `Quarantine` — карантин на диске переноса |
| `sentinel/sentinel.zip` | Встроенный антивирус Sentinel (собирается `scripts/update-sentinel.sh`) |
| `Hardware.cs` | Профиль железа, контроллер диска (VMD/RST), совместимость |
| `Errors.cs` | Разбор ошибок robocopy, `Problem`, текстовые отчёты |
| `Util.cs` | `Sh.Run` (запуск утилит, остановка и пауза **всего дерева процессов**), `Native` (P/Invoke), `Fmt`, `Log`, `Json` |

### Процессы, пауза, отмена

`Sh.Run(exe, args, onLine, enc, started, stop)` запускает утилиту без окна. Если `stop()` вернул `true`, утилита
завершается **вместе со всеми дочерними процессами** (Toolhelp32): DismHost.exe, копии, которые утилита запустила.
Долгие утилиты (robocopy, dism, sfc, MpCmdRun, Sentinel.Cli) регистрируются: «Пауза» замораживает их деревья
(`NtSuspendProcess`), «Отменить» и закрытие окна их завершают. Этапы, которые работают внутри программы, ждут
в `Runner.WaitWhilePaused()`. Короткие служебные команды (reg, разметка диска через PowerShell, bcdedit) не
замораживаются и не прерываются.

## Этапы «Развернуть образ» (`CloneApplyFlow`)

1. **Проверка**: образ на месте, UEFI.
2. **Драйверы этого компьютера**: `dism /Online /Export-Driver` в `images\drivers-<ПК>`. Обязательно найти драйвер контроллера диска (Intel VMD/RST).
3. **Раздел под клон**: основная Windows ужимается (`Resize-Partition`), новый раздел NTFS «Windows 2», метка `xfer-fresh.marker`.
4. **Разворачивание**: `dism /Apply-Image /CheckIntegrity`. Этап 1 (проверка целостности) показывает прогресс по
   прочитанным DISM байтам (`GetProcessIoCounters`), этап 2 — по процентам DISM. При обрыве USB-диска (в т. ч. `0x800701B1`)
   программа ждёт диск и повторяет (до 3 попыток), раздел перед повтором форматируется.
5. **Sentinel: поиск вирусов в клоне** — см. ниже. Можно пропустить кнопкой.
6. **Проверка на майнеры и следы вирусов** (`Security.cs`): Защитник по папкам клона + реестр клона + файлы. Найденные файлы уходят в карантин, затем повторная проверка. Можно пропустить.
7. **Сверка системных файлов**: `sfc /scannow /offbootdir /offwindir`, процент и оставшееся время.
8. **Драйверы и настройка**: `dism /Image /Add-Driver /Recurse` с прогрессом «драйвер N из M» (из вывода DISM и
   из новых строк `C:\Windows\Logs\DISM\dism.log` — «Successfully proccessed driver package»), текущий драйвер и
   время на нём, оставшееся время; сброс `MountedDevices`, включение storahci/stornvme.
9. **Загрузчик**: `bcdboot`, имя пункта, основная Windows — по умолчанию и первой, таймаут 15 с; BitLocker приостанавливается на одну перезагрузку.

«Снять клон» (`CloneCaptureFlow`): место → проверка на вирусы (для работающей Windows дополнительно
`sentinel audit-startup` и `audit-config`) → теневая копия → `dism /Capture-Image /CheckIntegrity` (повтор при обрыве диска) → проверка образа → `clone.json`.

## Sentinel вместо Kaspersky

Раньше клон проверял Kaspersky Virus Removal Tool: его каждый раз скачивали с сайта Kaspersky, он сканировал весь раздел
(15+ минут на 100 ГБ) и перезапускал себя копией из `%TEMP%`. Теперь используется **Sentinel** — собственный антивирус
пользователя (репозиторий `masterRaizer/Antivirus-tool`, встроен релиз 1.3.11).

- **Где берётся**: Sentinel **встроен в Перенос.exe** (ресурс `Transfer.sentinel.zip`). При первой проверке он распаковывается в
  `%LOCALAPPDATA%\Perenos\sentinel\<отпечаток>\` (старые версии удаляются) — ничего скачивать и устанавливать не нужно.
  Если на компьютере уже установлен Sentinel (`%ProgramData%\Sentinel` есть), встроенный использует его данные — обновлённую базу угроз
  и белый список; если нет — свои данные в `%LOCALAPPDATA%\Perenos\sentinel-data` (`--data-dir`), чтобы не оставлять следов в ProgramData.
  Запасные пути, если сборка без архива: `C:\Program Files\Sentinel\Sentinel.Cli.exe`, затем `tools\Sentinel\Sentinel.Cli.exe` на диске переноса.
  Если Sentinel не нашёлся нигде, этап помечается жёлтым с понятным текстом, перенос продолжается.
- **Что проверяется**: файлы в корне клона, все папки верхнего уровня (кроме `Windows`, `Windows.old`, `System Volume Information`,
  `Recovery` и служебных `$…`), а внутри `Windows` — `Temp`, `Tasks`, `Setup`, `System32\Tasks`, `System32\drivers`,
  `System32\GroupPolicy`, `SysWOW64\GroupPolicy`, `System32\spool\drivers`. Остальные системные файлы сверяет с оригиналами `sfc`.
- **Как запускается** (на каждую папку отдельно):
  `Sentinel.Cli.exe scan <папка> [--no-recurse] --report-json data\logs\sentinel-<время>\NN-<папка>.json --report-html …html`.
  Коды: 0 — чисто, 1 — находки, 2 — ошибка.
- **Прогресс**: Sentinel печатает строку на каждый файл (`[чисто   ] путь`) — по ним считается «проверено N из M файлов»;
  M считается параллельно обходом тех же папок (без ссылок-переходов, как и у Sentinel). Видны текущий файл, число находок и оставшееся время.
- **Разбор**: основной источник — JSON-отчёт (`Areas[].Checked`, `Findings[]`: `Name`, `Path`, `Rule`, `Severity`, `Description`,
  `ActionStatus`); формат сверен с `Sentinel.Core/Reporting/ReportModels.cs`. Если JSON не прочитался (другая версия),
  находки берутся из текстового вывода (`[УГРОЗА  ] путь` + строки с уровнем и правилом).
- **Что делается с находками**. `--remediate` Sentinel **не используется**: его порог карантина — Medium с учётом эвристик,
  а на клоне другой сборки Windows файлы, подписанные через каталог, выглядят неподписанными, и эвристика могла бы вынести
  из клона нормальные файлы. Поэтому:
  - определённые срабатывания (сигнатура, база хешей, облачная репутация, EICAR; уровень Medium и выше) переносятся в
    **карантин на диске переноса** `data\quarantine\<время>\…\файл.quarantine` с журналом `manifest.txt` (откуда взят файл) —
    при ложном срабатывании файл можно вернуть;
  - находки одной эвристики (`Heuristic.*`) попадают в отчёт как «подозрительное», файлы остаются на месте;
  - если определённую угрозу убрать не удалось, загрузчик не записывается.
- **Пропуск и отмена**: кнопка «Пропустить проверку» и «Отменить» завершают Sentinel вместе с дочерними процессами.

Собственная проверка (`CloneFlowBase.Disarm`) тоже больше не удаляет файлы, а переносит их в тот же карантин.

## Структура диска переноса

```
Перенос.exe
data\files\…            копии дисков (перенос данных)
data\reg, data\info     реестр, списки программ, Wi-Fi, задания
data\logs\app-*.log     журнал программы; robocopy-*.log; security-*.txt; sentinel-<время>\ (JSON/HTML Sentinel)
data\quarantine\<время>\ карантин найденного в клонах + manifest.txt
data\xfer.id            метка диска (диск находится и под другой буквой)
images\<ПК>-C-<дата>\windows.wim + clone.json
images\drivers-<ПК>\    драйверы компьютера, на который разворачивается клон
templates\*.wim|*.esd   шаблоны Windows
tools\Sentinel\         (не нужно: Sentinel встроен в программу) запасная копия Sentinel
```
