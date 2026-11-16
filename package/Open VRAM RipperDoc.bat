@echo off
rem Opens the VRAM RipperDoc app. Nothing in your game folder is changed until you press Apply in the app.
cd /d "%~dp0"
if not exist "%~dp0VramRipperdoc.exe" goto :missing
rem "start" gives the tool its own (minimized) window, so this one can close right away.
start "VRAM RipperDoc" "%~dp0VramRipperdoc.exe" --gui
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
