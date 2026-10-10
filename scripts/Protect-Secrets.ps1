<#
.SYNOPSIS
    Encrypts EDITH's secrets with Windows DPAPI (current user) and removes the clear-text files.

.DESCRIPTION
    For every *.txt in C:\EDITH\secrets: writes <name>.txt.dpapi, decrypts it back, and only
    if the round trip matches exactly, deletes the clear-text file. EDITH reads the .dpapi copy.
    Only this Windows account can decrypt it; keep your own backup of the keys elsewhere.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\EDITH\scripts\Protect-Secrets.ps1
#>
[CmdletBinding()]
param(
    [string]$SecretsDirectory = 'C:\EDITH\secrets'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

$files = Get-ChildItem -Path $SecretsDirectory -Filter '*.txt' -File -ErrorAction SilentlyContinue
if (-not $files) {
    Write-Host "No clear-text secrets in $SecretsDirectory."
    return
}

foreach ($file in $files) {
    $clear = [System.IO.File]::ReadAllText($file.FullName).Trim()
    if ([string]::IsNullOrWhiteSpace($clear)) {
        Write-Host "Skip empty $($file.Name)"
        continue
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($clear)
    $protected = [System.Security.Cryptography.ProtectedData]::Protect(
        $bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    $target = "$($file.FullName).dpapi"
    [System.IO.File]::WriteAllBytes($target, $protected)

    $roundTrip = [System.Text.Encoding]::UTF8.GetString(
        [System.Security.Cryptography.ProtectedData]::Unprotect(
            [System.IO.File]::ReadAllBytes($target), $null,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser))

    if ($roundTrip -ne $clear) {
        Remove-Item $target -Force
        throw "Round trip failed for $($file.Name); the clear-text file was kept."
    }

    try {
        Remove-Item $file.FullName -Force -ErrorAction Stop
        Write-Host "Protected $($file.Name) -> $([System.IO.Path]::GetFileName($target))"
    }
    catch {
        # No delete right on the file (e.g. a restricted ACL): wiping its content is enough,
        # EDITH reads the .dpapi copy and treats an empty file as no clear-text secret.
        [System.IO.File]::WriteAllText($file.FullName, '')
        Write-Host "Protected $($file.Name) -> $([System.IO.Path]::GetFileName($target)) (clear-text file emptied: no delete permission)"
    }
}
