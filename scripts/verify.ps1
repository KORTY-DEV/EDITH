<#
.SYNOPSIS
    Build the whole solution and run every test without touching the running EDITH or the GPU.

.DESCRIPTION
    - Builds into a scratch output folder, so a running Edith.App.exe that locks
      src\Edith.App\bin\Release does not break the build.
    - Points EDITH_BRAIN_CONFIG at a disabled brain config, so no test can
      reach Ollama and load a model onto the GPU.
    - Uses limited MSBuild parallelism so the machine stays usable.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\verify.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $env:TEMP 'edith-verify'),
    [int]$MaxCpu = 2
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repo 'EDITH.slnx'
$output = (Join-Path $OutputRoot 'out') + '\'

New-Item -ItemType Directory -Force $OutputRoot | Out-Null
$brainOff = Join-Path $OutputRoot 'brain-disabled.json'
Set-Content -Path $brainOff -Value '{ "enabled": false }' -Encoding utf8

$previousBrainConfig = $env:EDITH_BRAIN_CONFIG
$env:EDITH_BRAIN_CONFIG = $brainOff
$cloudOff = Join-Path $OutputRoot 'cloud-disabled.json'
Set-Content -Path $cloudOff -Value '{ "enabled": false }' -Encoding utf8
$previousCloudConfig = $env:EDITH_CLOUD_CONFIG
$env:EDITH_CLOUD_CONFIG = $cloudOff
try {
    Write-Host "== build ($solution -> $output)"
    dotnet build $solution -c Release -nologo -v:q "-m:$MaxCpu" "-p:BaseOutputPath=$output"
    if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

    Write-Host '== test (brain disabled, no Ollama/GPU)'
    dotnet test $solution -c Release --no-build -nologo -v:q "-m:$MaxCpu" "-p:BaseOutputPath=$output"
    if ($LASTEXITCODE -ne 0) { throw "tests failed ($LASTEXITCODE)" }

    Write-Host '== verify OK'
}
finally {
    $env:EDITH_BRAIN_CONFIG = $previousBrainConfig
    $env:EDITH_CLOUD_CONFIG = $previousCloudConfig
}
