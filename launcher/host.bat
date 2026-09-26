@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0host.ps1"
if errorlevel 1 echo. & echo FAILED, see the message above.
pause
