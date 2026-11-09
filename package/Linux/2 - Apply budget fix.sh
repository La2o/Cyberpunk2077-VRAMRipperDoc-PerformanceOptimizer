#!/usr/bin/env bash
# Scans your mods, shows what it would shrink (count, size, time) and asks before writing anything.
# The fix is ONE extra archive; your mods are never modified. Undo with "Undo budget fix.sh".
exec "$(dirname "$(readlink -f "$0")")/ripperdoc.sh"     --say "This can add ONE new file to archive/pc/mod with half-size copies of your heaviest textures."     --say "Your mods are not modified. It scans first, then shows what it would do and asks before writing."     --say "" --make-budget-archive --ask --pause "$@"
