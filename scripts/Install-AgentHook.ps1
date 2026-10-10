<#
.SYNOPSIS
    Lets EDITH know when an AI agent finishes, for "выключи компьютер, когда агент закончит".

.DESCRIPTION
    Adds two command hooks to the settings file of an AI agent that supports hooks (JSON with a "hooks" section, events Stop and UserPromptSubmit):
      - Stop             -> touches C:\EDITH\data\signals\agent-stop  (the agent finished a turn)
      - UserPromptSubmit -> touches C:\EDITH\data\signals\agent-busy  (new work started)
    EDITH shuts down only after a stop marker newer than any busy marker has stayed quiet
    for two minutes. Existing hooks are kept; the settings file is backed up first.
    Run it yourself once; EDITH never edits files outside C:\EDITH on its own.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Install-AgentHook.ps1 -SettingsPath C:\Users\me\.agent\settings.json
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Install-AgentHook.ps1 -SettingsPath C:\Users\me\.agent\settings.json -Uninstall
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SettingsPath,
    [string]$SignalsDirectory = 'C:\EDITH\data\signals',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$marker = 'EDITH-SIGNAL'

function New-TouchCommand([string]$file) {
    $path = Join-Path $SignalsDirectory $file
    # The trailing comment identifies EDITH's hooks for -Uninstall and re-runs.
    "powershell -NoProfile -Command `"New-Item -ItemType Directory -Force '$SignalsDirectory' | Out-Null; Set-Content -Path '$path' -Value (Get-Date -Format o)`" # $marker"
}

function Get-Settings {
    if (-not (Test-Path $SettingsPath)) { return [pscustomobject]@{} }
    $text = Get-Content -Raw -Path $SettingsPath
    if ([string]::IsNullOrWhiteSpace($text)) { return [pscustomobject]@{} }
    return $text | ConvertFrom-Json
}

function Remove-EdithHooks($groups) {
    @($groups | Where-Object {
        -not (@($_.hooks) | Where-Object { $_.command -like "*$marker*" })
    })
}

$settings = Get-Settings
if (-not $settings.PSObject.Properties['hooks']) {
    $settings | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{})
}

foreach ($event in 'Stop', 'UserPromptSubmit') {
    $existing = @()
    if ($settings.hooks.PSObject.Properties[$event]) { $existing = Remove-EdithHooks $settings.hooks.$event }

    if (-not $Uninstall) {
        $file = if ($event -eq 'Stop') { 'agent-stop' } else { 'agent-busy' }
        $existing += [pscustomobject]@{
            hooks = @([pscustomobject]@{ type = 'command'; command = (New-TouchCommand $file) })
        }
    }

    if ($settings.hooks.PSObject.Properties[$event]) {
        $settings.hooks.$event = $existing
    }
    else {
        $settings.hooks | Add-Member -NotePropertyName $event -NotePropertyValue $existing
    }
}

if (Test-Path $SettingsPath) {
    $backup = "$SettingsPath.edith-backup-$(Get-Date -Format yyyyMMdd-HHmmss)"
    Copy-Item $SettingsPath $backup
    Write-Host "Backup: $backup"
}
else {
    New-Item -ItemType Directory -Force (Split-Path -Parent $SettingsPath) | Out-Null
}

$json = $settings | ConvertTo-Json -Depth 20
[System.IO.File]::WriteAllText($SettingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
New-Item -ItemType Directory -Force $SignalsDirectory | Out-Null

if ($Uninstall) { Write-Host "EDITH hooks removed from $SettingsPath" }
else { Write-Host "EDITH hooks installed in $SettingsPath (signals: $SignalsDirectory)" }
