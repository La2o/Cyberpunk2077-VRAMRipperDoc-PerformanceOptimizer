@echo off
rem Scans your mods, shows what it would shrink (count, size, time) and asks before writing anything.
rem The fix is ONE extra archive; your mods are never modified. Undo with "Undo budget fix.bat".
cd /d "%~dp0"
echo This can add ONE new file to archive\pc\mod with half-size copies of your heaviest textures.
echo Your mods are not modified. It scans first, then shows what it would do and asks before writing.
echo.
"%~dp0VramRipperdoc.exe" --make-budget-archive --ask --pause
