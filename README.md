# Windows Transfer («Перенос»)

WPF-утилита (C#, .NET Framework 4.7.2): перенос данных между ПК, снятие клона Windows в WIM и его разворачивание через DISM.

- Исходники: `src/` (`Transfer.csproj`)
- Инструкция для пользователя: `ПРОЧТИ.txt`

## Сборка
На рабочей машине есть только старый `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe` (C# 5):

```
MSBuild.exe src\Transfer.csproj /p:Configuration=Release /p:LangVersion=Default
```

Поэтому код пишется в стиле C# 5: без `out var`, без `$""`, без `?.`, без expression-bodied членов и т. п.
Собранный `Transfer.exe` кладётся на внешний диск как `F:\Windows Transfer\Перенос.exe`.

## Структура диска переноса
`images\<PC>-C-<date>\windows.wim` + `clone.json`, логи — `data\logs\app-*.log`, утилиты — `tools\`.
