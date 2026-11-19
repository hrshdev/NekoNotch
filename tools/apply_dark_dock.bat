@echo off
setlocal
echo Requesting administrator privileges...
powershell -NoProfile -Command "Start-Process powershell -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"\"%~dp0apply_dark_dock.ps1\"\"' -Verb RunAs"
echo Done.
