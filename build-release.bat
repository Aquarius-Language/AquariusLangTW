@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Prefer PowerShell 7, then fall back to the built-in Windows PowerShell 5.1.
set "PACKAGE_POWERSHELL="
for /f "delims=" %%P in ('where pwsh.exe 2^>nul') do if not defined PACKAGE_POWERSHELL set "PACKAGE_POWERSHELL=%%P"
if not defined PACKAGE_POWERSHELL if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "PACKAGE_POWERSHELL=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined PACKAGE_POWERSHELL if exist "%LOCALAPPDATA%\Microsoft\PowerShell\7\pwsh.exe" set "PACKAGE_POWERSHELL=%LOCALAPPDATA%\Microsoft\PowerShell\7\pwsh.exe"
if not defined PACKAGE_POWERSHELL set "PACKAGE_POWERSHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
"%PACKAGE_POWERSHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\package-release.ps1" %*
set "PACKAGE_EXIT_CODE=%ERRORLEVEL%"
if not "%PACKAGE_EXIT_CODE%"=="0" echo [ERROR] Packaging failed. See the error and build log above.
exit /b %PACKAGE_EXIT_CODE%
