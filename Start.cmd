@echo off
cd /d "%~dp0"
echo Starting Meeting Evidence at http://localhost:5086
echo Keep this window open. Press Ctrl+C to stop.
powershell -NoProfile -File "%~dp0Start.ps1"
pause
