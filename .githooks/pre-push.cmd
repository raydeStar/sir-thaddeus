@echo off
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\dev\test.ps1"
if errorlevel 1 exit /b 1
exit /b 0
