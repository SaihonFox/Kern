@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\kernw.ps1" %*
exit /b %ERRORLEVEL%
