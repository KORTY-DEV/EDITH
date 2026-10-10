# Установка EDITH «в один клик» с вопросами по-русски: проверка Windows, .NET, установка, модели, ключ GigaChat, запуск.
# Запускается файлом «Установка.cmd» из распакованного архива (можно и так: powershell -ExecutionPolicy Bypass -File Setup.ps1).
param(
    [switch]$Yes   # не задавать вопросов: ответы «по умолчанию» (автозапуск, сторож и модели — да)
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$install = Join-Path $here 'Install-Release.ps1'
if (-not (Test-Path $install)) { $install = Join-Path $here 'scripts\Install-Release.ps1' }
$models = Join-Path $here 'scripts\Install-Models.ps1'
if (-not (Test-Path $models)) { $models = Join-Path $here 'Install-Models.ps1' }

function Say([string]$text, [string]$color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Ask([string]$question, [bool]$default = $true) {
    if ($Yes) { return $default }
    $hint = if ($default) { 'Д/н' } else { 'д/Н' }
    $answer = Read-Host "$question [$hint]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
    return $answer.Trim().ToLowerInvariant().StartsWith('д') -or $answer.Trim().ToLowerInvariant().StartsWith('y')
}

Say ''
Say '  E.D.I.T.H. — установка' 'Cyan'
Say '  Займёт 10–20 минут, почти всё время — скачивание моделей.' 'DarkGray'
Say ''

# 1. Windows
$os = [Environment]::OSVersion.Version
if ($os.Major -lt 10 -or $os.Build -lt 22000) {
    Say "Внимание: у вас Windows сборки $($os.Build). EDITH проверена только на Windows 11; на другой системе возможны сбои." 'Yellow'
    if (-not (Ask 'Продолжить всё равно?' $false)) { return }
}
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Нужна 64-разрядная Windows.' }

# 2. .NET 10 Desktop Runtime
function Test-DotNetDesktop {
    try { return [bool](& dotnet --list-runtimes 2>$null | Select-String 'Microsoft.WindowsDesktop.App 10\.') } catch { return $false }
}
Say '1/5  Проверяю .NET 10 Desktop Runtime…' 'White'
if (-not (Test-DotNetDesktop)) {
    Say '     Не найден. Он нужен для запуска EDITH.' 'Yellow'
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget -and (Ask '     Установить его сейчас через winget (около 60 МБ)?')) {
        & winget install -e --id Microsoft.DotNet.DesktopRuntime.10 --accept-package-agreements --accept-source-agreements
    }
    if (-not (Test-DotNetDesktop)) {
        Say '     Установите .NET 10 Desktop Runtime вручную: https://dotnet.microsoft.com/download/dotnet/10.0 — и запустите установку снова.' 'Yellow'
        Start-Process 'https://dotnet.microsoft.com/download/dotnet/10.0'
        if (-not (Ask '     Продолжить без него (EDITH не запустится, пока его нет)?' $false)) { return }
    }
}
else { Say '     есть' 'Green' }

# 3. Установка
Say '2/5  Устанавливаю EDITH в C:\EDITH…' 'White'
$autostart = Ask '     Запускать EDITH вместе с Windows?'
$watchdog = Ask '     Перезапускать EDITH, если она упадёт (рекомендуется)?'
$args = @('-NoStart')
if ($autostart) { $args += '-Autostart' }
if ($watchdog) { $args += '-Watchdog' }
& powershell -NoProfile -ExecutionPolicy Bypass -File $install @args
if ($LASTEXITCODE -ne 0) { throw 'Установка не удалась (см. сообщение выше).' }

# 4. Модели и голос
Say '3/5  Модели и голос' 'White'
if (Test-Path $models) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $models -Check | Out-Host
    if (Ask '     Скачать недостающие модели и поставить голос сейчас (около 1 ГБ; нужен интернет и Python 3.11–3.13)?') {
        $py = Get-Command py -ErrorAction SilentlyContinue
        if (-not $py -and -not (Get-Command python -ErrorAction SilentlyContinue)) {
            $winget = Get-Command winget -ErrorAction SilentlyContinue
            if ($winget -and (Ask '     Python не найден. Установить Python 3.13 через winget?')) {
                & winget install -e --id Python.Python.3.13 --accept-package-agreements --accept-source-agreements
                $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
            }
        }
        & powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\EDITH\scripts\Install-Models.ps1'
    }
    else { Say '     Хорошо. Позже: powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Install-Models.ps1' 'DarkGray' }
}

# 5. Ключ GigaChat
Say '4/5  Облачный мозг GigaChat (по желанию)' 'White'
Say '     Бесплатный ключ даёт быстрые ответы; без него EDITH думает локальной моделью. Инструкция: C:\EDITH\docs\GIGACHAT.md' 'DarkGray'
if (-not $Yes -and (Ask '     Вставить ключ сейчас?' $false)) {
    $secure = Read-Host '     Ключ авторизации (ввод скрыт)' -AsSecureString
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)).Trim()
    if ($plain.Length -ge 20) {
        Add-Type -AssemblyName System.Security
        New-Item -ItemType Directory -Force 'C:\EDITH\secrets' | Out-Null
        $bytes = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($plain), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        [IO.File]::WriteAllBytes('C:\EDITH\secrets\gigachat-auth-key.txt.dpapi', $bytes)
        $cloud = 'C:\EDITH\config\cloud.json'
        if (Test-Path $cloud) {
            $text = [IO.File]::ReadAllText($cloud)
            [IO.File]::WriteAllText($cloud, ($text -replace '"enabled":\s*(true|false)', '"enabled": true'), (New-Object Text.UTF8Encoding $false))
        }
        Say '     Ключ сохранён (зашифрован для вашей учётной записи).' 'Green'
    }
    else { Say '     Это не похоже на ключ; пропускаю. Вставить можно позже: значок EDITH в трее → Настройки.' 'Yellow' }
}

# 6. Запуск
Say '5/5  Запускаю EDITH…' 'White'
$exe = 'C:\EDITH\app\current\Edith.App.exe'
if (Test-Path $exe) { Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) }
Say ''
Say '  Готово. В трее (у часов) появился значок EDITH; скажите «Эдит». Если чего-то не хватает,' 'Green'
Say '  на вкладке «Сейчас» окна EDITH (двойной щелчок по значку) написано, что именно.' 'Green'
Say ''
if (-not $Yes) { Read-Host 'Нажмите Enter, чтобы закрыть это окно' | Out-Null }
