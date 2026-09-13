@echo off
title Black Ops III Audio Fix - Installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0BO3AudioFixInstaller.ps1"
if errorlevel 1 (
  echo.
  echo The installer exited with an error.
  pause
)
