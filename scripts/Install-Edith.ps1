# Собирает EDITH в C:\EDITH\app\current, закрывает запущенную EDITH, ставит новую версию
# и делает ярлыки «EDITH» на рабочем столе и в меню «Пуск». Затем запускает EDITH.
#
#   powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Install-Edith.ps1
#
# EDITH запускается из app\current, а не из bin\Release: сборка больше не упирается в
# «файл занят другим процессом». Прошлая версия остаётся в app\previous (откат — -Rollback).
param(
    [switch]$NoStart,
    [switch]$Autostart,
    [switch]$NoAutostart,
    [switch]$Watchdog,
    [switch]$NoWatchdog,
    [switch]$Rollback
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$appRoot = Join-Path $repo 'app'
$current = Join-Path $appRoot 'current'
$next = Join-Path $appRoot 'next'
$previous = Join-Path $appRoot 'previous'
$exeName = 'Edith.App.exe'

function Stop-Edith {
    $running = @(Get-Process -Name 'Edith.App' -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }

    Write-Host '== закрываю EDITH'
    try {
        $exit = [System.Threading.EventWaitHandle]::OpenExisting('Local\EDITH.App.Exit')
        [void]$exit.Set()
        $exit.Dispose()
    }
    catch {
        # Старая сборка без сигнала выхода — закроется ниже принудительно.
    }

    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name 'Edith.App' -ErrorAction SilentlyContinue).Count -gt 0) {
        Start-Sleep -Milliseconds 300
    }

    $left = @(Get-Process -Name 'Edith.App' -ErrorAction SilentlyContinue)
    if ($left.Count -gt 0) {
        Write-Host '   не закрылась сама за 20 с — завершаю процесс'
        $left | Stop-Process -Force
        Start-Sleep -Milliseconds 800
    }
}

# EDITH's own Ollama server (port 11435) may sit in the app folder (older builds started it there):
# stop it so the folder can be replaced; EDITH starts it again on the first question.
function Stop-OwnOllama {
    $own = Get-NetTCPConnection -State Listen -LocalPort 11435,11436 -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique
    foreach ($id in $own) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -like 'ollama*') {
            Write-Host '== останавливаю собственный сервер Ollama EDITH (11435)'
            # /T: its llama-server child holds the folder as working directory too.
            & taskkill /PID $id /T /F | Out-Null
            Start-Sleep -Milliseconds 500
        }
    }
}

function New-Shortcut([string]$path, [string]$target) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $target
    $link.WorkingDirectory = Split-Path -Parent $target
    $link.IconLocation = (Join-Path (Split-Path -Parent $target) 'edith.ico') + ',0'
    $link.Description = 'E.D.I.T.H. — голосовой помощник'
    $link.Save()
}

New-Item -ItemType Directory -Force $appRoot | Out-Null

# Personal configs are not in the repository: each config\X.example.json becomes config\X.json when it is missing.
foreach ($example in Get-ChildItem (Join-Path $repo 'config') -Filter '*.example.json' -ErrorAction SilentlyContinue) {
    $real = Join-Path $example.DirectoryName ($example.Name -replace '\.example\.json$', '.json')
    if (-not (Test-Path $real)) { Copy-Item $example.FullName $real; Write-Host "== config: $($example.Name) -> $(Split-Path -Leaf $real)" }
}

if ($Rollback) {
    if (-not (Test-Path $previous)) { throw 'Нет прошлой версии для отката (app\previous).' }
    Stop-Edith
    Stop-OwnOllama
    if (Test-Path $next) { Remove-Item -Recurse -Force $next }
    Rename-Item $current 'next'
    Rename-Item $previous 'current'
    Rename-Item $next 'previous'
    Write-Host '== откат: вернула прошлую версию'
}
else {
    Write-Host '== сборка (EDITH пока работает)'
    if (Test-Path $next) { Remove-Item -Recurse -Force $next }
    $buildOut = (Join-Path $appRoot '.build') + '\'
    dotnet publish (Join-Path $repo 'src\Edith.App\Edith.App.csproj') -c Release -nologo -v:q -o $next "-p:BaseOutputPath=$buildOut"
    if ($LASTEXITCODE -ne 0) { throw "сборка не удалась ($LASTEXITCODE) — старая EDITH не тронута" }

    Stop-Edith
    Stop-OwnOllama

    if (Test-Path $previous) { Remove-Item -Recurse -Force $previous }
    try {
        if (Test-Path $current) { Rename-Item $current 'previous' -ErrorAction Stop }
        Rename-Item $next 'current'
    }
    catch {
        # Some process keeps the folder itself open (its working directory): replace the contents instead of renaming.
        Write-Host '   папка занята другим процессом — заменяю содержимое'
        New-Item -ItemType Directory -Force $previous | Out-Null
        robocopy $current $previous /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        robocopy $next $current /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw 'не удалось скопировать новую версию' }
        Remove-Item -Recurse -Force $next
    }
    Write-Host "== установлено в $current"
}

$exe = Join-Path $current $exeName
$desktop = [Environment]::GetFolderPath('Desktop')
$programs = [Environment]::GetFolderPath('Programs')
New-Shortcut (Join-Path $desktop 'EDITH.lnk') $exe
New-Shortcut (Join-Path $programs 'EDITH.lnk') $exe
Write-Host '== ярлыки: рабочий стол и меню «Пуск»'

# Старт вместе с Windows: ярлык в папке автозагрузки (то же делает галочка в настройках EDITH).
$startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) 'EDITH.lnk'
if ($Autostart) {
    New-Shortcut $startupLink $exe
    Write-Host '== автозапуск включён'
}
elseif ($NoAutostart -and (Test-Path $startupLink)) {
    Remove-Item $startupLink
    Write-Host '== автозапуск выключен'
}

# Сторож: раз в минуту проверяет, не упала ли EDITH (не закрыта, а именно упала), и поднимает её снова.
if ($Watchdog) {
    $action = New-ScheduledTaskAction -Execute 'wscript.exe' -Argument '//B //Nologo "C:\EDITH\scripts\Edith-Watchdog.vbs"'
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 2)
    Register-ScheduledTask -TaskName 'EDITH-Watchdog' -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
    Write-Host '== сторож включён (задание EDITH-Watchdog)'
}
elseif ($NoWatchdog) {
    Unregister-ScheduledTask -TaskName 'EDITH-Watchdog' -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host '== сторож выключен'
}

if (-not $NoStart) {
    Start-Process -FilePath $exe -WorkingDirectory $current
    Write-Host '== EDITH запущена (значок в трее)'
}
