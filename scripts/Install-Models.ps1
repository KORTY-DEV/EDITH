# Скачивает недостающие модели EDITH из официальных источников и ставит окружение голоса.
#
#   powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Install-Models.ps1            # всё недостающее
#   powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Install-Models.ps1 -Check     # только показать, чего не хватает
#   ... -SkipVoice      не ставить Python-окружение голоса
#   ... -Browser        дополнительно скачать собственный Chromium EDITH (для браузерных инструментов)
#
# Уже скачанное не трогается и не скачивается повторно. Всего около 0,7 ГБ моделей и 0,5 ГБ окружения голоса.
# Модели wake (слово «Эдит») и VAD лежат в самом архиве EDITH; здесь только то, что нельзя раздавать вместе с ней.
param(
    [switch]$Check,
    [switch]$SkipVoice,
    [switch]$Browser
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root 'models'

# Источник, куда положить, как проверить, что готово, размер в мегабайтах (для сообщений).
$items = @(
    @{ Name = 'Голос Silero v5 (русский)';      Url = 'https://models.silero.ai/models/tts/ru/v5_ru.pt';
       Target = 'tts\silero\v5_ru.pt'; Mb = 139; Note = 'лицензия CC BY-NC-SA 4.0 (некоммерческое использование)' },
    @{ Name = 'Распознавание речи GigaAM v3';   Url = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-ctc-giga-am-v3-russian-2025-12-16.tar.bz2';
       Target = 'stt\sherpa-onnx-nemo-ctc-giga-am-v3-russian-2025-12-16\model.int8.onnx'; Archive = 'stt'; Mb = 156 },
    @{ Name = 'Vosk (русская малая модель)';    Url = 'https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip';
       Target = 'wake\vosk-model-small-ru-0.22\am\final.mdl'; Archive = 'wake'; Mb = 44 },
    @{ Name = 'Конец фразы (Smart Turn v3.2)';  Url = 'https://huggingface.co/pipecat-ai/smart-turn-v3/resolve/main/smart-turn-v3.2-cpu.onnx';
       Target = 'smart-turn\smart-turn-v3.2-cpu.onnx'; Mb = 8 },
    @{ Name = 'Смысловой поиск (e5, модель)';   Url = 'https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/onnx/model_quantized.onnx';
       Target = 'embedding\model_quantized.onnx'; Mb = 113 },
    @{ Name = 'Смысловой поиск (e5, словарь)';  Url = 'https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/tokenizer.json';
       Target = 'embedding\tokenizer.json'; Mb = 17 },
    @{ Name = 'Смысловой поиск (e5, настройки)'; Url = 'https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/tokenizer_config.json';
       Target = 'embedding\tokenizer_config.json'; Mb = 1 }
)

function Test-Done($item) { Test-Path (Join-Path $models $item.Target) }

Write-Host '== модели EDITH'
$missing = @($items | Where-Object { -not (Test-Done $_) })
foreach ($item in $items) {
    $state = if (Test-Done $item) { 'есть   ' } else { 'НЕТ    ' }
    Write-Host ("  {0} {1}" -f $state, $item.Name)
}
foreach ($need in 'wake\custom-v3\edith-stage1.onnx', 'vad\silero_vad.onnx') {
    if (-not (Test-Path (Join-Path $models $need))) { Write-Host "  НЕТ     $need — должен быть в архиве EDITH; распакуйте архив полностью" }
}
$venvPython = Join-Path $root 'data\tts-venv\Scripts\python.exe'
$voiceReady = Test-Path $venvPython
Write-Host ("  {0} Окружение голоса (Python)" -f $(if ($voiceReady) { 'есть   ' } else { 'НЕТ    ' }))
if ($Check) { return }

New-Item -ItemType Directory -Force $models | Out-Null
foreach ($item in $missing) {
    Write-Host "== скачиваю: $($item.Name) (~$($item.Mb) МБ)"
    if ($item.Note) { Write-Host "   $($item.Note)" }
    $target = Join-Path $models $item.Target
    $temp = Join-Path $env:TEMP ('edith-model-' + [guid]::NewGuid().ToString('N') + [IO.Path]::GetExtension($item.Url))
    & (Join-Path $env:SystemRoot 'System32\curl.exe') -L --fail --retry 4 --retry-delay 3 -C - -o $temp $item.Url
    if ($LASTEXITCODE -ne 0) { throw "не удалось скачать $($item.Url)" }

    if ($item.Archive) {
        $dest = Join-Path $models $item.Archive
        New-Item -ItemType Directory -Force $dest | Out-Null
        if ($temp -like '*.zip') { Expand-Archive -LiteralPath $temp -DestinationPath $dest -Force }
        else { & (Join-Path $env:SystemRoot 'System32\tar.exe') -xf $temp -C $dest; if ($LASTEXITCODE -ne 0) { throw 'tar не смог распаковать архив' } }
        Remove-Item $temp -Force
    }
    else {
        New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
        Move-Item -LiteralPath $temp -Destination $target -Force
    }
    if (-not (Test-Path $target)) { throw "после загрузки не найден $($item.Target)" }
}

if (-not $SkipVoice -and -not $voiceReady) {
    Write-Host '== окружение голоса (Python + torch для процессора + ударения)'
    $py = $null
    foreach ($candidate in @('py -3.13', 'py -3.12', 'py -3.11', 'python')) {
        try {
            $parts = $candidate.Split(' ')
            $v = & $parts[0] $parts[1..($parts.Length - 1)] --version 2>$null
            if ($LASTEXITCODE -eq 0 -and $v -match 'Python 3\.(1[1-3])') { $py = $parts; break }
        } catch { }
    }
    if (-not $py) { throw 'Нужен Python 3.11–3.13 (python.org). Поставьте и запустите скрипт снова.' }
    & $py[0] $py[1..($py.Length - 1)] -m venv (Join-Path $root 'data\tts-venv')
    & $venvPython -m pip install --upgrade pip
    & $venvPython -m pip install -r (Join-Path $PSScriptRoot 'tts\requirements.txt') --extra-index-url https://download.pytorch.org/whl/cpu
    if ($LASTEXITCODE -ne 0) { throw 'pip не смог поставить пакеты голоса' }

    # Модели ударений (RUAccent) докачиваются при первом запуске: делаем это сейчас, чтобы EDITH не ждала.
    $ru = Join-Path $models 'ruaccent'
    New-Item -ItemType Directory -Force $ru | Out-Null
    & $venvPython -c "from ruaccent import RUAccent; a = RUAccent(); a.load(omograph_model_size='tiny2.1', use_dictionary=True, workdir=r'$ru'); print('ruaccent ok')"
    if ($LASTEXITCODE -ne 0) { Write-Host '   ударения докачаются при первом запуске голоса' }
}

if ($Browser) {
    Write-Host '== собственный Chromium EDITH'
    $env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $models 'browsers'
    $driver = Join-Path $root 'app\current\playwright.ps1'
    if (Test-Path $driver) { & powershell -NoProfile -ExecutionPolicy Bypass -File $driver install chromium }
    else { Write-Host '   не найден app\current\playwright.ps1 (EDITH не установлена?)' }
}

Write-Host '== готово. Перезапустите EDITH (значок в трее → Перезапустить).'
