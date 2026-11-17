# Сбор сведений о программах и настройках (вызывается из Перенос.exe). Строки «##…» читает приложение.
param([string]$Info, [string]$Reg)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'SilentlyContinue'
function Step($id) { Write-Output "##STEP:$id" }
function Note($t) { Write-Output "##INFO:$t" }
function Warn($t) { Write-Output "##WARN:$t" }
foreach ($d in $Info, $Reg) { New-Item -ItemType Directory -Force $d | Out-Null }

# ---------------------------------------------------------------- программы
Step 'apps'
$os = Get-CimInstance Win32_OperatingSystem; $cs = Get-CimInstance Win32_ComputerSystem
@{ Computer = $env:COMPUTERNAME; User = $env:USERNAME; Profile = $env:USERPROFILE; SystemDrive = $env:SystemDrive; Date = (Get-Date).ToString('s')
   Windows = $os.Caption; Version = $os.Version; Model = "$($cs.Manufacturer) $($cs.Model)" } | ConvertTo-Json | Set-Content (Join-Path $Info 'machine.json') -Encoding UTF8

$keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
$apps = Get-ItemProperty $keys | Where-Object { $_.DisplayName -and -not $_.SystemComponent } |
    Select-Object DisplayName, DisplayVersion, Publisher, InstallLocation, InstallDate, UninstallString | Sort-Object DisplayName -Unique
$apps | Export-Csv (Join-Path $Info 'programs.csv') -NoTypeInformation -Encoding UTF8 -Delimiter ';'
$store = Get-AppxPackage | Where-Object { -not $_.IsFramework } | Select-Object Name, Version, PackageFamilyName, SignatureKind, InstallLocation
$store | Export-Csv (Join-Path $Info 'store-apps.csv') -NoTypeInformation -Encoding UTF8 -Delimiter ';'
Note "Программ: $(@($apps).Count), приложений из Магазина: $(@($store).Count)"

$winget = (Get-Command winget).Source
if (-not $winget) { $w = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'; if (Test-Path $w) { $winget = $w } }
if ($winget) {
    # winget не умеет писать в путь с русскими буквами — выгружаем во временный файл и переносим
    $wtmp = Join-Path $env:PUBLIC 'winget-export.json'; Remove-Item $wtmp -Force
    & $winget export -o $wtmp --accept-source-agreements --disable-interactivity 2>&1 | Out-File (Join-Path $Info 'winget-export.log') -Encoding UTF8
    if (Test-Path $wtmp) {
        Copy-Item $wtmp (Join-Path $Info 'winget.json') -Force; Remove-Item $wtmp -Force
        $n = ((Get-Content (Join-Path $Info 'winget.json') -Raw | ConvertFrom-Json).Sources | ForEach-Object { $_.Packages.Count } | Measure-Object -Sum).Sum
        Note "Из них установятся сами на новом компьютере: $n"
    } else { Warn 'Не удалось составить список для автоустановки — программы придётся ставить по списку вручную' }
} else { Warn 'Установщик winget не найден — программы на новом компьютере придётся ставить вручную по списку' }

# ---------------------------------------------------------------- реестр
Step 'reg'
function Export-SubKeys($regPath, $psPath, $outDir, $skip) {
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $n = 0
    Get-ChildItem $psPath | ForEach-Object {
        $name = $_.PSChildName
        if ($skip -contains $name) { return }
        $safe = ($name -replace '[\\/:*?"<>|]', '_')
        & reg.exe export "$regPath\$name" (Join-Path $outDir "$safe.reg") /y 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { $n++ }
    }
    return $n
}
$skipVendors = @('Microsoft', 'Classes', 'Policies', 'Wow6432Node', 'WOW6432Node', 'RegisteredApplications', 'Clients', 'ODBC', 'Intel', 'Realtek', 'Nvidia Corporation', 'NVIDIA Corporation', 'AMD', 'ATI Technologies', 'Khronos', 'DefaultUserEnvironment', 'Windows', 'Dolby', 'Huawei', 'HUAWEI')
$n1 = Export-SubKeys 'HKCU\Software' 'Registry::HKEY_CURRENT_USER\Software' (Join-Path $Reg 'hkcu') $skipVendors
$n2 = Export-SubKeys 'HKLM\SOFTWARE' 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE' (Join-Path $Reg 'hklm') $skipVendors
$n3 = Export-SubKeys 'HKLM\SOFTWARE\WOW6432Node' 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node' (Join-Path $Reg 'hklm32') $skipVendors
$ms = Join-Path $Reg 'hkcu_ms'; New-Item -ItemType Directory -Force $ms | Out-Null
foreach ($k in 'Office', 'VisualStudio', 'VSCommon', 'Terminal Server Client', 'Command Processor', 'Notepad', 'Windows\CurrentVersion\Explorer\Advanced') {
    & reg.exe export "HKCU\Software\Microsoft\$k" (Join-Path $ms (($k -replace '[\\ ]', '_') + '.reg')) /y 2>&1 | Out-Null
}
& reg.exe export 'HKCU\Environment' (Join-Path $Reg 'user-environment.reg') /y 2>&1 | Out-Null
$full = Join-Path $Reg 'full'; New-Item -ItemType Directory -Force $full | Out-Null
& reg.exe export 'HKCU' (Join-Path $full 'HKCU-full.reg') /y 2>&1 | Out-Null
& reg.exe export 'HKLM\SOFTWARE' (Join-Path $full 'HKLM-SOFTWARE-full.reg') /y 2>&1 | Out-Null
Note "Сохранены настройки $($n1 + $n2 + $n3) программ"

# ---------------------------------------------------------------- Wi-Fi, задания, прочее
Step 'misc'
$wifi = Join-Path $Info 'wifi'; New-Item -ItemType Directory -Force $wifi | Out-Null
& netsh wlan export profile key=clear folder="$wifi" 2>&1 | Out-Null
$tasks = Join-Path $Info 'tasks'; New-Item -ItemType Directory -Force $tasks | Out-Null
Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\Microsoft\*' } | ForEach-Object {
    try { Export-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath | Set-Content (Join-Path $tasks (($_.TaskName -replace '[\\/:*?"<>|]', '_') + '.xml')) -Encoding Unicode } catch {}
}
Copy-Item "$env:SystemRoot\System32\drivers\etc\hosts" (Join-Path $Info 'hosts') -Force
[Environment]::GetEnvironmentVariables('User').GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" } | Set-Content (Join-Path $Info 'env-user.txt') -Encoding UTF8
[Environment]::GetEnvironmentVariables('Machine').GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" } | Set-Content (Join-Path $Info 'env-machine.txt') -Encoding UTF8
Note "Сетей Wi-Fi: $(@(Get-ChildItem $wifi -Filter *.xml).Count), заданий планировщика: $(@(Get-ChildItem $tasks -Filter *.xml).Count)"
Write-Output '##DONE'
