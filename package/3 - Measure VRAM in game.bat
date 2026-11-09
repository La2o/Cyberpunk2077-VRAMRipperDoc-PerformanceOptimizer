@echo off
rem Records the game's real VRAM use while you play. Run once before and once after a change.
cd /d "%~dp0"
if not exist "%~dp0VramRipperdoc.exe" goto :missing
set "label="
set /p "label=Name this measurement (for example: before or after): "
if "%label%"=="" set "label=session"
echo.
echo Now start the game and load a save. Play for a minute or two, then close the game to finish.
"%~dp0VramRipperdoc.exe" --live --label "%label%"
echo.
pause
goto :eof

:missing
echo.
echo VramRipperdoc.exe is missing from this folder, so the tool can't start.
echo.
echo Most often your antivirus removed it. Open Windows Security,
echo "Virus & threat protection", then "Protection history". If VramRipperdoc.exe
echo is listed, choose Actions, then Restore (or Allow on device), and double-click
echo this file again.
echo.
echo If it isn't listed, download the mod again from
echo https://www.nexusmods.com/cyberpunk2077/mods/36878
echo and unpack the whole zip into a normal folder, for example your Desktop.
echo.
pause
exit /b 1
