# EDITH watchdog: restarts EDITH when she crashed (not when she was closed properly).
# The app writes "running" to data\run.state at start and "clean" on a proper exit.
# Run once a minute by the scheduled task EDITH-Watchdog (registered by Install-Edith.ps1 -Watchdog).
$state = 'C:\EDITH\data\run.state'
$exe = 'C:\EDITH\app\current\Edith.App.exe'
if (-not (Test-Path $state) -or -not (Test-Path $exe)) { return }
if ((Get-Content $state -Raw).Trim() -ne 'running') { return }
if (Get-Process -Name 'Edith.App' -ErrorAction SilentlyContinue) { return }

$log = 'C:\EDITH\data\crashes.log'
Add-Content -Path $log -Value ('{0:yyyy-MM-dd HH:mm:ss} EDITH was not running while state=running - restarting' -f (Get-Date))
Start-Sleep -Seconds 5
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
