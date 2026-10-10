<#
.SYNOPSIS
    Устанавливает обученную custom wake-модель EDITH ("Эдит") в runtime.

.DESCRIPTION
    Берёт edith.onnx от trainer-а, проверяет его тем же кодом, каким
    runtime EDITH загружает модель (metadata + прогон реального pipeline
    + проверка, что модель не "стреляет" на тишине), и атомарно
    устанавливает в C:\EDITH\models\wake\custom.

    Что делает:
      * проверяет файл (существует, не пустой, валидный ONNX);
      * проверяет контракт: [1,16,96] float32 -> [1,1] probability;
      * при необходимости кладёт generic melspectrogram.onnx и
        embedding_model.onnx (из data\wake-training\models);
      * делает backup существующего edith.onnx в custom\backup;
      * заменяет edith.onnx атомарно (File.Replace / Move);
      * запущенный EDITH подхватывает модель сам в течение нескольких
        секунд, без перезапуска (в config\wake.json engine = auto/custom).

    Чего НЕ делает: не запускает обучение, не меняет trainer output,
    датасет и wake-training, не трогает Ollama и другие модели.

.PARAMETER ModelPath
    Путь к обученному edith.onnx.

.PARAMETER ModelsRoot
    Куда устанавливать. По умолчанию C:\EDITH\models\wake\custom.

.PARAMETER GenericModelsDir
    Откуда брать generic melspectrogram.onnx / embedding_model.onnx.

.PARAMETER GenericOnly
    Только подготовить generic mel/embedding модели (ModelPath не нужен).

.PARAMETER Force
    Заменить generic mel/embedding, даже если они уже установлены и валидны.

.EXAMPLE
    .\Install-WakeModel.ps1 -ModelPath C:\EDITH\wake-training\custom-wakeword-trainer\edith.onnx
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Position = 0)]
    [string] $ModelPath,

    [string] $ModelsRoot = 'C:\EDITH\models\wake\custom',

    [string] $GenericModelsDir = 'C:\EDITH\data\wake-training\models',

    [switch] $GenericOnly,

    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

# -WhatIf: показываем, что было бы сделано, ничего не меняя.
# Preference сбрасываем, иначе read-only cmdlet'ы (Get-FileHash,
# Resolve-Path) тоже уходят в режим "what if".
$DryRun = [bool] $WhatIfPreference
$WhatIfPreference = $false

function Confirm-Action([string] $Target, [string] $Action) {
    if ($DryRun) {
        Write-Host "What if: $Action -> $Target" -ForegroundColor Yellow
        return $false
    }

    return $true
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CheckProject = Join-Path $RepoRoot 'src\Edith.WakeCheck\Edith.WakeCheck.csproj'
$CheckOutput = Join-Path $RepoRoot 'src\Edith.WakeCheck\bin\Release\net10.0-windows'
$CheckExe = Join-Path $CheckOutput 'Edith.WakeCheck.exe'


function Write-Step([string] $Text) {
    Write-Host "  $Text" -ForegroundColor Gray
}

function Fail([string] $Text) {
    Write-Host ''
    Write-Host "INSTALL FAILED: $Text" -ForegroundColor Red
    exit 1
}

# Любая неожиданная ошибка = понятный отказ и exit code 1,
# а не полуустановленная модель.
trap {
    Fail $_.Exception.Message
}

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}


# Проверка тем же кодом, что использует runtime EDITH.
function Ensure-CheckTool {
    $sourcesNewer = $false

    if (Test-Path -LiteralPath $CheckExe) {
        # apphost (.exe) не перезаписывается при пересборке dll,
        # поэтому сравниваем с самой библиотекой.
        $auditDll = Join-Path $CheckOutput 'Edith.Audio.dll'
        $exeTime = if (Test-Path -LiteralPath $auditDll) {
            (Get-Item -LiteralPath $auditDll).LastWriteTimeUtc
        } else {
            [datetime]::MinValue
        }

        $newest = Get-ChildItem -Path (Join-Path $RepoRoot 'src\Edith.Audio'),
                                (Join-Path $RepoRoot 'src\Edith.WakeCheck') `
                                -Recurse -Include *.cs, *.csproj -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1

        if ($newest -and $newest.LastWriteTimeUtc -gt $exeTime) {
            $sourcesNewer = $true
        }
    }

    if ((Test-Path -LiteralPath $CheckExe) -and -not $sourcesNewer) {
        return
    }

    Write-Step 'building Edith.WakeCheck (validator) ...'

    $output = & dotnet build $CheckProject -c Release --nologo -v q 2>&1

    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        Fail 'could not build Edith.WakeCheck validator (dotnet build failed)'
    }
}

function Invoke-Validate([string] $Model, [string] $Mel, [string] $Embedding) {
    $output = & $CheckExe validate --model $Model --mel $Mel --embedding $Embedding 2>&1

    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = ($output | Out-String)
    }
}

function Install-Atomically([string] $Source, [string] $Destination) {
    $directory = Split-Path -Parent $Destination
    $temporary = Join-Path $directory ('.' + [IO.Path]::GetFileName($Destination) + '.tmp-' + [guid]::NewGuid().ToString('N'))

    try {
        Copy-Item -LiteralPath $Source -Destination $temporary -Force

        if ((Get-Sha256 $temporary) -ne (Get-Sha256 $Source)) {
            throw "copy verification failed for $Destination"
        }

        if (Test-Path -LiteralPath $Destination) {
            # Атомарная замена на одном томе.
            [IO.File]::Replace($temporary, $Destination, [NullString]::Value)
        }
        else {
            [IO.File]::Move($temporary, $Destination)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
}


Write-Host ''
Write-Host '=== EDITH custom wake model installer ===' -ForegroundColor Cyan

if (-not $GenericOnly) {
    if ([string]::IsNullOrWhiteSpace($ModelPath)) {
        Fail 'specify -ModelPath <path to trained edith.onnx>'
    }

    # ---- 1. Исходный файл --------------------------------------------
    if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) {
        Fail "model file not found: $ModelPath"
    }

    $ModelPath = (Resolve-Path -LiteralPath $ModelPath).ProviderPath
    $sourceInfo = Get-Item -LiteralPath $ModelPath

    if ($sourceInfo.Length -lt 1024) {
        Fail "file is too small to be a wake model ($($sourceInfo.Length) bytes): $ModelPath"
    }

    if ($sourceInfo.Length -gt 64MB) {
        Fail "file is too large to be a wake model ($($sourceInfo.Length) bytes): $ModelPath"
    }

    Write-Step "source : $ModelPath ($($sourceInfo.Length) bytes)"
}

if (-not $DryRun) {
    New-Item -ItemType Directory -Force -Path $ModelsRoot | Out-Null
}

Write-Step "target : $ModelsRoot"

$melDest = Join-Path $ModelsRoot 'melspectrogram.onnx'
$embeddingDest = Join-Path $ModelsRoot 'embedding_model.onnx'
$edithDest = Join-Path $ModelsRoot 'edith.onnx'

Ensure-CheckTool

# ---- 2. Generic mel / embedding ---------------------------------------
$genericCopied = @()

foreach ($name in 'melspectrogram.onnx', 'embedding_model.onnx') {
    $destination = Join-Path $ModelsRoot $name
    $source = Join-Path $GenericModelsDir $name

    $needsInstall = $Force.IsPresent -or -not (Test-Path -LiteralPath $destination)

    if (-not $needsInstall) {
        Write-Step "$name already installed (kept)"
        continue
    }

    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        Fail "generic model missing: $source (needed because $destination is not installed)"
    }

    if (Confirm-Action $destination "install generic $name") {
        Install-Atomically $source $destination
        $genericCopied += $name
        Write-Step "installed generic $name"
    }
}

if ($GenericOnly) {
    Write-Host ''
    Write-Host 'GENERIC MODELS READY' -ForegroundColor Green
    Write-Host "  mel       : $melDest"
    Write-Host "  embedding : $embeddingDest"
    Write-Host '  edith.onnx is not installed by -GenericOnly; EDITH keeps using Vosk until it is.'
    exit 0
}

if ($DryRun) {
    Write-Host ''
    Write-Host 'Dry run: validation, backup and installation were skipped; nothing was changed.' -ForegroundColor Yellow
    exit 0
}

# ---- 3. Проверка нового edith.onnx ДО установки -----------------------
Write-Step 'validating model against the EDITH runtime contract ...'

$validation = Invoke-Validate -Model $ModelPath -Mel $melDest -Embedding $embeddingDest

if ($validation.ExitCode -ne 0) {
    Write-Host $validation.Output
    Fail 'model does not satisfy the EDITH wake contract; nothing was installed'
}

Write-Step 'contract OK: input [1,16,96] float32 -> output [1,1] probability, silent-room baseline OK'

# ---- 4. Backup + атомарная установка ----------------------------------
$newHash = Get-Sha256 $ModelPath
$backupPath = $null

if (Test-Path -LiteralPath $edithDest) {
    $oldHash = Get-Sha256 $edithDest

    if ($oldHash -eq $newHash) {
        Write-Host ''
        Write-Host 'ALREADY INSTALLED (identical edith.onnx)' -ForegroundColor Green
        Write-Host "  sha256 : $newHash"
        exit 0
    }

    $backupDir = Join-Path $ModelsRoot 'backup'
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

    $backupPath = Join-Path $backupDir ('edith.onnx.' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak')

    if (Confirm-Action $edithDest "backup to $backupPath") {
        Copy-Item -LiteralPath $edithDest -Destination $backupPath -Force
        Write-Step "backup : $backupPath"
    }
}

if (Confirm-Action $edithDest 'install edith.onnx') {
    Install-Atomically $ModelPath $edithDest

    if ((Get-Sha256 $edithDest) -ne $newHash) {
        Fail "post-install hash mismatch for $edithDest"
    }

    # ---- 5. Проверка уже установленного набора --------------------------
    $final = Invoke-Validate -Model $edithDest -Mel $melDest -Embedding $embeddingDest

    if ($final.ExitCode -ne 0) {
        if ($backupPath) {
            Install-Atomically $backupPath $edithDest
            Fail 'installed set failed final validation; previous edith.onnx restored'
        }

        Remove-Item -LiteralPath $edithDest -Force
        Fail 'installed set failed final validation; edith.onnx removed'
    }
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Green
Write-Host ' EDITH WAKE MODEL INSTALLED'              -ForegroundColor Green
Write-Host '========================================' -ForegroundColor Green
Write-Host "  model     : $edithDest"
Write-Host "  sha256    : $newHash"
Write-Host "  mel       : $melDest"
Write-Host "  embedding : $embeddingDest"

if ($backupPath) {
    Write-Host "  backup    : $backupPath"
}

Write-Host ''
Write-Host 'A running EDITH switches to the custom detector automatically within a few seconds'
Write-Host '(see C:\EDITH\data\diagnostics\wake-engine.txt -> "custom"). Nothing to restart.'
Write-Host 'Trainer output and dataset were not modified.'
exit 0
