@echo off
setlocal
cd /d "%~dp0"
rem Baut den Monitor, traegt ihn in den Windows-Autostart ein und startet ihn
call "%~dp0build.bat" nostart
if errorlevel 1 exit /b 1
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v AIUsageMonitor /t REG_SZ /d "\"%~dp0AIUsageMonitor.exe\"" /f >nul
if errorlevel 1 (echo Autostart-Eintrag fehlgeschlagen.) else (echo Autostart eingerichtet.)
start "" "%~dp0AIUsageMonitor.exe"
timeout /t 3 >nul
