@echo off
setlocal
cd /d "%~dp0"
".\bin\me3.exe" launch --auto-detect --show-logos=false -p ".\sekiro-bingo.me3"
if errorlevel 1 pause
endlocal
