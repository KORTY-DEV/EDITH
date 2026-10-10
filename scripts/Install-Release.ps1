# Установка и обновление EDITH из релизного архива (не нужен .NET SDK, только .NET 10 Desktop Runtime).
#
#   1. Распакуйте архив куда угодно (например, в Загрузки).
#   2. powershell -ExecutionPolicy Bypass -File <распакованная папка>\Install-Release.ps1 [-Autostart] [-Watchdog]
#
# Устанавливает в C:\EDITH. Обновление безопасно: ваши настройки (config), память и журнал (data), ключи (secrets) и
# модели (models) НЕ перезаписываются; прошлая версия приложения остаётся в app\previous для отката.
param(
    [string]$Target = 'C:\EDITH',
    [switch]$Autostart,
    [switch]$Watchdog,
    [switch]$NoStart,
    [switch]$NoShortcuts,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
if ((Test-Path (Join-Path $Target '.git')) -and -not $Force) { throw "В $Target лежит git-репозиторий разработки: установка поверх него запрещена (параметр -Force снимает защиту)." }
$exeName = 'Edith.App.exe'
if (-not (Test-Path (Join-Path $src "app\current\$exeName"))) { throw 'В архиве не найден app\current\Edith.App.exe: распакован ли он полностью?' }
if ($Target -ne 'C:\EDITH') { Write-Host "Внимание: EDITH рассчитана на папку C:\EDITH; $Target годится только для проверки." }

function Stop-Edith {
    $running = @(Get-Process -Name 'Edith.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $Target 'app\*') })
    if ($running.Count -eq 0) { return }
    Write-Host '== закрываю запущенную EDITH'
    try { $exit = [System.Threading.EventWaitHandle]::OpenExisting('Local\EDITH.App.Exit'); [void]$exit.Set(); $exit.Dispose() } catch { }
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name 'Edith.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $Target 'app\*') }).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    @(Get-Process -Name 'Edith.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $Target 'app\*') }) | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

New-Item -ItemType Directory -Force $Target | Out-Null
$inPlace = ((Resolve-Path $src).Path.TrimEnd('\') -ieq (Resolve-Path $Target).Path.TrimEnd('\'))

if (-not $inPlace) {
    if ($Target -eq 'C:\EDITH') { Stop-Edith }
    $current = Join-Path $Target 'app\current'
    $previous = Join-Path $Target 'app\previous'
    New-Item -ItemType Directory -Force (Join-Path $Target 'app') | Out-Null

    # Новое приложение рядом, затем быстрая подмена: если копирование упадёт, старая версия остаётся рабочей.
    $next = Join-Path $Target 'app\next'
    if (Test-Path $next) { Remove-Item -Recurse -Force $next }
    Copy-Item -Recurse (Join-Path $src 'app\current') $next
    if (Test-Path $previous) { Remove-Item -Recurse -Force $previous }
    if (Test-Path $current) { Move-Item $current $previous }
    Move-Item $next $current
    Write-Host '== приложение установлено (прошлая версия — app\previous)'

    # Скрипты, документы, лицензия: можно обновлять. Конфиги: только те, которых ещё нет.
    foreach ($dir in 'scripts', 'docs') {
        if (Test-Path (Join-Path $src $dir)) {
            New-Item -ItemType Directory -Force (Join-Path $Target $dir) | Out-Null
            Copy-Item -Recurse -Force (Join-Path $src "$dir\*") (Join-Path $Target $dir)
        }
    }
    foreach ($file in 'README.md', 'LICENSE') { if (Test-Path (Join-Path $src $file)) { Copy-Item -Force (Join-Path $src $file) $Target } }
    New-Item -ItemType Directory -Force (Join-Path $Target 'config') | Out-Null
    foreach ($cfg in Get-ChildItem (Join-Path $src 'config') -File) {
        $dst = Join-Path $Target "config\$($cfg.Name)"
        if (-not (Test-Path $dst)) { Copy-Item $cfg.FullName $dst }
        elseif ((Get-FileHash $cfg.FullName).Hash -ne (Get-FileHash $dst).Hash) { Copy-Item $cfg.FullName "$dst.new" -Force }
    }
    # Модели wake и VAD — часть EDITH (в архиве); остальные скачивает Install-Models.ps1.
    if (Test-Path (Join-Path $src 'models')) {
        foreach ($m in Get-ChildItem (Join-Path $src 'models') -Directory) {
            if (-not (Test-Path (Join-Path $Target "models\$($m.Name)"))) { Copy-Item -Recurse $m.FullName (Join-Path $Target "models\$($m.Name)") }
            else { Copy-Item -Recurse -Force (Join-Path $m.FullName '*') (Join-Path $Target "models\$($m.Name)") }
        }
    }
}

# Личные настройки: config\X.example.json -> config\X.json, если рабочего файла ещё нет.
foreach ($example in Get-ChildItem (Join-Path $Target 'config') -Filter '*.example.json' -ErrorAction SilentlyContinue) {
    $real = Join-Path $example.DirectoryName ($example.Name -replace '\.example\.json$', '.json')
    if (-not (Test-Path $real)) { Copy-Item $example.FullName $real; Write-Host "== конфиг: $(Split-Path -Leaf $real)" }
}
foreach ($d in 'data', 'secrets', 'models') { New-Item -ItemType Directory -Force (Join-Path $Target $d) | Out-Null }

$exe = Join-Path $Target "app\current\$exeName"
function New-Shortcut([string]$path, [string]$target) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $target
    $link.WorkingDirectory = Split-Path -Parent $target
    $link.IconLocation = (Join-Path (Split-Path -Parent $target) 'edith.ico') + ',0'
    $link.Description = 'E.D.I.T.H.'
    $link.Save()
}
if (-not $NoShortcuts) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'EDITH.lnk') $exe
    New-Shortcut (Join-Path ([Environment]::GetFolderPath('Programs')) 'EDITH.lnk') $exe
    Write-Host '== ярлыки: рабочий стол и меню Пуск'
    if ($Autostart) { New-Shortcut (Join-Path ([Environment]::GetFolderPath('Startup')) 'EDITH.lnk') $exe; Write-Host '== автозапуск включён' }
    if ($Watchdog) {
        $action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument ('//B //Nologo "' + (Join-Path $Target 'scripts\Edith-Watchdog.vbs') + '"')
        $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
        $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
        Register-ScheduledTask -TaskName 'EDITH-Watchdog' -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
        Write-Host '== сторож включён'
    }
}

Write-Host ''
Write-Host '== дальше: powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Install-Models.ps1   (модели и окружение голоса)'
Write-Host '== и ключ GigaChat: значок EDITH в трее -> Настройки -> GigaChat (инструкция: docs\GIGACHAT.md)'
if (-not $NoStart -and -not $NoShortcuts) { Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe); Write-Host '== EDITH запущена (значок в трее)' }
