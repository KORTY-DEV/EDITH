# Удаляет EDITH: закрывает её, убирает ярлыки, автозапуск и сторожа.
#
#   powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Uninstall.ps1               # приложение удаляется, ваши данные остаются
#   powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Uninstall.ps1 -RemoveData   # и данные тоже (память, журнал, ключи, модели)
#
# Без -RemoveData остаются config, data, secrets и models: при повторной установке EDITH узнает вас.
# Ничего за пределами папки EDITH, ярлыков и задания сторожа скрипт не трогает.
param(
    [string]$Target = 'C:\EDITH',
    [switch]$RemoveData,
    [switch]$NoShortcuts,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if ((Test-Path (Join-Path $Target '.git')) -and -not $Force) { throw "В $Target лежит git-репозиторий разработки: установка поверх него запрещена (параметр -Force снимает защиту)." }

$running = @(Get-Process -Name 'Edith.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $Target 'app\*') })
if ($running.Count -gt 0) {
    Write-Host '== закрываю EDITH'
    try { $exit = [System.Threading.EventWaitHandle]::OpenExisting('Local\EDITH.App.Exit'); [void]$exit.Set(); $exit.Dispose() } catch { }
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name 'Edith.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $Target 'app\*') }).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    @(Get-Process -Name 'Edith.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like (Join-Path $Target 'app\*') }) | Stop-Process -Force
}

if (-not $NoShortcuts) {
    Unregister-ScheduledTask -TaskName 'EDITH-Watchdog' -Confirm:$false -ErrorAction SilentlyContinue
    foreach ($place in 'Desktop', 'Programs', 'Startup') {
        $link = Join-Path ([Environment]::GetFolderPath($place)) 'EDITH.lnk'
        if (Test-Path $link) { Remove-Item $link }
    }
    Write-Host '== ярлыки, автозапуск и сторож убраны'
}

# Приложение, скрипты и документы — всегда; данные пользователя — только по -RemoveData.
foreach ($path in 'app', 'scripts', 'docs', 'README.md', 'LICENSE') {
    $full = Join-Path $Target $path
    if (Test-Path $full) { Remove-Item -Recurse -Force $full }
}
if ($RemoveData) {
    foreach ($path in 'config', 'data', 'secrets', 'models') {
        $full = Join-Path $Target $path
        if (Test-Path $full) { Remove-Item -Recurse -Force $full }
    }
    if ((Get-ChildItem $Target -Force -ErrorAction SilentlyContinue).Count -eq 0) { Remove-Item $Target -Force }
    Write-Host '== удалено всё, включая ваши данные'
}
else {
    Write-Host "== приложение удалено; ваши данные сохранены в $Target (config, data, secrets, models)"
}
