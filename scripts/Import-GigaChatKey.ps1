# Переносит ключ GigaChat из .env другого проекта в EDITH, сразу зашифрованным (DPAPI).
# Ключ не выводится на экран и не пишется открытым текстом.
#
#   powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Import-GigaChatKey.ps1
param([Parameter(Mandatory)][string]$EnvFile)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

function Get-EnvValue([string]$name) {
    $line = Get-Content -LiteralPath $EnvFile -Encoding UTF8 | Where-Object { $_ -match "^\s*$name\s*=" } | Select-Object -First 1
    if (-not $line) { return $null }
    return ($line -replace "^\s*$name\s*=\s*", '').Trim().Trim('"').Trim("'")
}

$key = Get-EnvValue 'GIGACHAT_CREDENTIALS'
if ([string]::IsNullOrWhiteSpace($key)) { throw "В $EnvFile нет GIGACHAT_CREDENTIALS" }

$target = 'C:\EDITH\secrets\gigachat-auth-key.txt.dpapi'
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
$bytes = [System.Security.Cryptography.ProtectedData]::Protect(
    [System.Text.Encoding]::UTF8.GetBytes($key), $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
[System.IO.File]::WriteAllBytes($target, $bytes)
Write-Host "Ключ GigaChat сохранён зашифрованным: $target"

# Тип доступа (физлицо/юрлицо) — не секрет, переносим в config\cloud.json.
$scope = Get-EnvValue 'GIGACHAT_SCOPE'
if ($scope -and $scope -ne 'GIGACHAT_API_PERS') {
    $cloud = 'C:\EDITH\config\cloud.json'
    $text = [System.IO.File]::ReadAllText($cloud)
    $text = $text -replace '"scope":\s*"[^"]*"', ('"scope": "' + $scope + '"')
    [System.IO.File]::WriteAllText($cloud, $text, (New-Object System.Text.UTF8Encoding $false))
    Write-Host "scope: $scope"
}
