' EDITH watchdog launcher: runs the PowerShell check completely hidden.
' wscript is a window-less program, and style 0 hides the console of the check, so nothing flashes on the screen
' (a scheduled task that starts powershell.exe directly shows a console for a moment every time).
Set shell = CreateObject("WScript.Shell")
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""C:\EDITH\scripts\Edith-Watchdog.ps1""", 0, False
