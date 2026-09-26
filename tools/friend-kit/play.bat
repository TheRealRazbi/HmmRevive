@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0play.ps1"
if errorlevel 1 echo. & echo FAILED, see the message above.
pause
