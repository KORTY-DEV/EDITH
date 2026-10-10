# Prepares a clean copy of the repository for publication: only tracked files, checked for forbidden words
# and secrets, with a fresh single-commit history (no old commit messages or trailers).
#
#   powershell -ExecutionPolicy Bypass -File scripts\Prepare-Release.ps1 [-Out C:\EDITH-release] [-Author "KORTY-DEV"] [-Email "id+KORTY-DEV@users.noreply.github.com"]
#
# It never pushes. After it finishes, review the folder and publish it yourself:
#   cd C:\EDITH-release ; gh repo create KORTY-DEV/EDITH --public --source . --push
param(
    [string]$Out = 'C:\EDITH-release',
    [string]$Author = 'KORTY-DEV',
    [string]$Email = 'KORTY-DEV@users.noreply.github.com',
    [string]$Message = 'E.D.I.T.H.: first public release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# Words that must not appear anywhere in the published tree (the pattern is assembled so this file does not match itself).
$forbidden = @('cla' + 'ude', 'anthr' + 'opic', 'cod' + 'ex', 'Co-Auth' + 'ored', 'korty-' + 'news', 'C:\Users\' + 'korty')
$secretPatterns = @('sk-[A-Za-z0-9]{20,}', 'Basic [A-Za-z0-9+/=]{30,}', '(?i)api[_-]?key\s*[:=]\s*["''][A-Za-z0-9]{16,}', 'ghp_[A-Za-z0-9]{20,}')

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null

$files = git ls-files
$problems = @()
foreach ($f in $files) {
    $src = Join-Path $repo $f
    if (-not (Test-Path -LiteralPath $src)) { continue }
    $dst = Join-Path $Out $f
    New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
    Copy-Item -LiteralPath $src -Destination $dst

    $isText = $f -match '\.(cs|csproj|slnx|md|json|ps1|py|txt|xaml|props|targets|gitignore|editorconfig)$' -or $f -eq 'LICENSE'
    if (-not $isText) { continue }
    $text = Get-Content -LiteralPath $src -Raw -ErrorAction SilentlyContinue
    if (-not $text) { continue }
    foreach ($w in $forbidden) {
        if ($text.IndexOf($w, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems += "forbidden '$w' in $f" }
    }
    foreach ($p in $secretPatterns) {
        if ($text -match $p) { $problems += "secret-like text ($p) in $f" }
    }
}

if ($problems.Count -gt 0) {
    $problems | Sort-Object -Unique | ForEach-Object { Write-Host "PROBLEM: $_" }
    throw "release check failed: $($problems.Count) problem(s); fix them and run again"
}

Push-Location $Out
git init -q -b main
git config user.name $Author
git config user.email $Email
git add -A
git commit -q -m $Message
Pop-Location

$count = @($files).Count
Write-Host "== clean copy: $Out ($count files), one commit by $Author"
Write-Host "== next: cd $Out ; gh repo create KORTY-DEV/EDITH --public --source . --push"
