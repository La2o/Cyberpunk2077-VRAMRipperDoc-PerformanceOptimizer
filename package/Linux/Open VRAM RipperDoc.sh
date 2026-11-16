#!/usr/bin/env bash
# Opens the VRAM RipperDoc app (a window; closing it ends the tool). Nothing is changed until you press Apply in it.
exec "$(dirname "$(readlink -f "$0")")/ripperdoc.sh" --gui "$@"
