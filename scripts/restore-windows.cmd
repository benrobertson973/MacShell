@echo off
rem Emergency restore: brings back the Windows taskbar/desktop if MacShell was killed.
taskkill /im MacShell.exe /f >nul 2>&1
set EXE=%~dp0..\publish\MacShell.exe
if not exist "%EXE%" set EXE=%~dp0..\bin\Release\net8.0-windows\MacShell.exe
if not exist "%EXE%" set EXE=%~dp0..\bin\Debug\net8.0-windows\MacShell.exe
"%EXE%" --restore
echo Windows taskbar and desktop restored.
