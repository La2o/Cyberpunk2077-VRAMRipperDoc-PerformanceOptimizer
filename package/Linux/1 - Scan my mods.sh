#!/usr/bin/env bash
# Scans your mods and opens the report. Nothing in your game folder is changed.
exec "$(dirname "$(readlink -f "$0")")/ripperdoc.sh" --pause "$@"
