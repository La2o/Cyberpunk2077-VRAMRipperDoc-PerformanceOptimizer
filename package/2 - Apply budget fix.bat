@echo off
rem Scans your mods, shows what it would shrink (count, size, time) and asks before writing anything.
rem The fix is ONE extra archive; your mods are never modified. Undo with "Undo budget fix.bat".
cd /d "%~dp0"
if not exist "%~dp0VramRipperdoc.exe" goto :missing
echo This can add ONE new file to archive\pc\mod with half-size copies of your heaviest textures.
echo Your mods are not modified. It scans first, then shows what it would do and asks before writing.
echo.
"%~dp0VramRipperdoc.exe" --make-budget-archive --ask --pause
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
