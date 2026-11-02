@echo off
rem Halves only the heaviest textures into ONE extra archive. Your mods are not modified.
rem Undo any time with "Undo budget fix.bat".
cd /d "%~dp0"
echo This adds ONE new file to archive\pc\mod with half-size copies of your heaviest textures.
echo Your mods are not modified. Run "Undo budget fix.bat" to remove it again.
echo.
choice /M "Apply the budget fix now"
if errorlevel 2 goto :eof
echo.
echo New clothing, cars and props only use VRAM while you wear, drive or spawn them.
choice /M "Also shrink their 4K textures to 2K"
if errorlevel 2 (
  "%~dp0VramRipperdoc.exe" --make-budget-archive --pause
) else (
  "%~dp0VramRipperdoc.exe" --make-budget-archive --include-new-items --pause
)
