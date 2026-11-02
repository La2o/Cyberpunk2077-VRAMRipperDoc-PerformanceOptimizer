@echo off
rem Removes the budget fix archive created by "2 - Apply budget fix.bat".
cd /d "%~dp0"
"%~dp0VramRipperdoc.exe" --remove-budget-archive
echo.
pause
