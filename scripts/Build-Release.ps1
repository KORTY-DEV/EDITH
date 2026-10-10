# Собирает релизный архив EDITH для GitHub Releases (без исходников, моделей и ваших настроек):
#
#   powershell -ExecutionPolicy Bypass -File scripts\Build-Release.ps1 [-Version 1.0.0] [-Out C:\EDITH-release\dist]
#
# В архиве: готовое приложение (framework-dependent, нужен .NET 10 Desktop Runtime), примеры конфигов, скрипты, документы.
# Перед упаковкой проверяется, что внутри нет ключей, личных путей и упоминаний, которые не должны публиковаться.
param(
    [string]$Version = '1.0.0',
    [string]$Out = 'C:\EDITH-release\dist',
    [string]$ModelsFrom = ''
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $ModelsFrom) { $ModelsFrom = Join-Path $repo 'models' }
$stage = Join-Path $env:TEMP "edith-release-$Version"
$name = "EDITH-$Version-win-x64"
$pack = Join-Path $stage 'EDITH'

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $pack 'app\current') | Out-Null

Write-Host '== публикация приложения'
dotnet publish (Join-Path $repo 'src\Edith.App\Edith.App.csproj') -c Release -r win-x64 --self-contained false -nologo -v:q `
    -o (Join-Path $pack 'app\current') "-p:Version=$Version" "-p:DebugType=none" "-p:DebugSymbols=false" "-p:BaseOutputPath=$(Join-Path $env:TEMP "edith-release-build-$Version")\"
if ($LASTEXITCODE -ne 0) { throw "публикация не удалась ($LASTEXITCODE)" }

Write-Host '== конфиги, скрипты, документы'
foreach ($dir in 'config', 'scripts', 'docs') {
    New-Item -ItemType Directory -Force (Join-Path $pack $dir) | Out-Null
}
# Только то, что отслеживается git: личные файлы (library.json, pulse.json, vault.json, tts.json, *.local.*) сюда не попадут.
Push-Location $repo
$tracked = git ls-files config scripts docs
Pop-Location
foreach ($f in $tracked) {
    $dst = Join-Path $pack $f
    New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo $f) -Destination $dst
}
# Модели, которые делает сама EDITH и которых нет в открытых источниках: слово «Эдит» (wake) и VAD.
foreach ($m in 'wake\custom', 'wake\custom-v3', 'vad') {
    $from = Join-Path $ModelsFrom $m
    if (Test-Path $from) { New-Item -ItemType Directory -Force (Join-Path $pack "models\$m") | Out-Null; Copy-Item (Join-Path $from '*') (Join-Path $pack "models\$m") -Recurse }
    else { throw "нет ${from}: релиз без модели слова «Эдит» бесполезен" }
}
Copy-Item (Join-Path $repo 'README.md') $pack
Copy-Item (Join-Path $repo 'LICENSE') $pack
Copy-Item (Join-Path $PSScriptRoot 'Install-Release.ps1') (Join-Path $pack 'Install-Release.ps1')
Copy-Item (Join-Path $PSScriptRoot 'Setup.ps1') (Join-Path $pack 'Setup.ps1')
Copy-Item (Join-Path $PSScriptRoot 'Install-Models.ps1') (Join-Path $pack 'Install-Models.ps1')
# Один двойной щелчок: «Установка.cmd» запускает Setup.ps1 (имя с русскими буквами, содержимое — только ASCII).
Copy-Item (Join-Path $PSScriptRoot 'Setup.cmd') (Join-Path $pack ([string]::Concat([char]0x423,[char]0x441,[char]0x442,[char]0x430,[char]0x43D,[char]0x43E,[char]0x432,[char]0x43A,[char]0x430) + '.cmd'))

Write-Host '== проверка на ключи и личные данные'
$forbidden = @('cla' + 'ude', 'anthr' + 'opic', 'cod' + 'ex', 'Co-Auth' + 'ored', 'korty-' + 'news', 'C:\Users\' + 'korty')
$secrets = @('sk-[A-Za-z0-9]{20,}', 'Basic [A-Za-z0-9+/=]{30,}', '(?i)api[_-]?key\s*[:=]\s*["''][A-Za-z0-9]{16,}', 'ghp_[A-Za-z0-9]{20,}')
$bad = @()
Get-ChildItem $pack -Recurse -File | ForEach-Object {
    if ($_.Name -match '\.(dpapi|env|pfx|key)$' -or $_.FullName -match '\\(secrets|data)\\') { $bad += "file $($_.FullName)" }
    if ($_.Extension -in '.json', '.md', '.txt', '.ps1', '.py', '.config') {
        $t = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue
        if ($t) {
            foreach ($w in $forbidden) { if ($t.IndexOf($w, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $bad += "'$w' in $($_.Name)" } }
            foreach ($p in $secrets) { if ($t -match $p) { $bad += "secret-like text in $($_.Name)" } }
        }
    }
}
if ($bad.Count -gt 0) { $bad | Sort-Object -Unique | ForEach-Object { Write-Host "PROBLEM: $_" }; throw 'проверка не пройдена' }

New-Item -ItemType Directory -Force $Out | Out-Null
$zip = Join-Path $Out "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
# .NET writes forward slashes in entry names (Compress-Archive of Windows PowerShell 5.1 wrote backslashes).
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
# Standard "hash *file" line with LF only, so both sha256sum -c and a human can read it.
[System.IO.File]::WriteAllText("$zip.sha256", "$($hash.ToLower()) *$name.zip`n", (New-Object System.Text.UTF8Encoding $false))
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "== готово: $zip ($mb МБ), SHA256 $hash"
