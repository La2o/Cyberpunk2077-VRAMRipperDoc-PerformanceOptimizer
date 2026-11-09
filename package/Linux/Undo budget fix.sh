#!/usr/bin/env bash
# Removes the budget fix archive created by "2 - Apply budget fix.sh".
exec "$(dirname "$(readlink -f "$0")")/ripperdoc.sh" --remove-budget-archive --pause "$@"
