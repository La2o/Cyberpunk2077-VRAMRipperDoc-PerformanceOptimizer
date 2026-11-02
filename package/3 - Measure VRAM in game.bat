@echo off
rem Records the game's real VRAM use while you play. Run once before and once after a change.
cd /d "%~dp0"
set "label="
set /p "label=Name this measurement (for example: before or after): "
if "%label%"=="" set "label=session"
echo.
echo Now start the game and load a save. Play for a minute or two, then close the game to finish.
"%~dp0VramRipperdoc.exe" --live --label "%label%"
echo.
pause
