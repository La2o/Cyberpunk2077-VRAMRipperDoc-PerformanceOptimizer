@echo off
rem Halves only the heaviest textures into ONE extra archive. Your mods are not modified.
rem Undo any time with "Undo budget fix.bat".
cd /d "%~dp0"
echo This adds one file to archive\pc\mod with smaller copies of your heaviest textures.
echo Your mods are not modified. Run "Undo budget fix.bat" to remove it again.
echo.
choice /M "Apply the budget fix now"
if errorlevel 2 goto :eof
"%~dp0VramRipperdoc.exe" --make-budget-archive --pause
