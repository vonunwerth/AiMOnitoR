@echo off
setlocal
cd /d "%~dp0"
rem Baut AIUsageMonitor.exe mit dem in Windows enthaltenen .NET-Framework-Compiler (keine Installation noetig)

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe nicht gefunden. Bitte .NET Framework 4.8 aktivieren ^(Windows-Features^).
  pause
  exit /b 1
)

rem Laufende Instanz beenden, damit die EXE ueberschrieben werden kann
taskkill /im AIUsageMonitor.exe /f >nul 2>&1

"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:AIUsageMonitor.exe /win32icon:src\app.ico ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll ^
  src\*.cs
if errorlevel 1 (
  echo.
  echo Build fehlgeschlagen.
  pause
  exit /b 1
)

echo.
echo Fertig: AIUsageMonitor.exe
if /i "%~1"=="nostart" exit /b 0
start "" "%~dp0AIUsageMonitor.exe"
