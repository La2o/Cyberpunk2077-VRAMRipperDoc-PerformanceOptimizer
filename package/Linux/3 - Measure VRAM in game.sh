#!/usr/bin/env bash
# Records the game's real VRAM use while you play. Run once before and once after a change.
exec "$(dirname "$(readlink -f "$0")")/ripperdoc.sh" --live --ask-label --pause "$@"
