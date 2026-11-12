@echo off
rem Beendet den Monitor und entfernt den Autostart-Eintrag
taskkill /im AIUsageMonitor.exe /f >/dev/null 2>&1
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v AIUsageMonitor /f >/dev/null 2>&1
echo AI Usage Monitor beendet und aus dem Autostart entfernt.
echo Einstellungen liegen unter %%APPDATA%%\AIUsageMonitor und koennen geloescht werden.
pause
