#!/usr/bin/env bash
# VRAM RipperDoc on Linux and Steam Deck.
#
# Runs VramRipperdoc.exe through Wine or Steam's Proton: the tool reads archives with the game's own Oodle, which only
# exists as a Windows DLL. This script fills in what the exe can't see from inside Wine (where the game, its settings
# and the graphics card are) and measures VRAM natively. The numbered shortcuts next to it call it; it also takes all
# of the exe's options, for example:  ./ripperdoc.sh --help   or   ./ripperdoc.sh --share
#
# Overrides: RIPPERDOC_WINE=<wine binary>  RIPPERDOC_GAME=<game folder>  RIPPERDOC_PREFIX=<Wine prefix for the tool>
set -u

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tool="$(dirname "$here")"
exe="$tool/VramRipperdoc.exe"
proc_root="${RIPPERDOC_PROC:-/proc}"   # test hooks
sys_root="${RIPPERDOC_SYS:-/sys}"

# Double-clicked in a file manager: there is no terminal to show anything in, so open one.
if [ ! -t 0 ] && [ -z "${RIPPERDOC_IN_TERMINAL:-}" ] && { [ -n "${DISPLAY:-}" ] || [ -n "${WAYLAND_DISPLAY:-}" ]; }; then
    export RIPPERDOC_IN_TERMINAL=1
    for t in konsole gnome-terminal xfce4-terminal mate-terminal alacritty kitty foot wezterm x-terminal-emulator xterm; do
        command -v "$t" >/dev/null 2>&1 || continue
        case "$t" in
            gnome-terminal) exec "$t" -- "$0" "$@" ;;
            xfce4-terminal|mate-terminal) exec "$t" -x "$0" "$@" ;;
            kitty|foot) exec "$t" "$0" "$@" ;;
            wezterm) exec "$t" start -- "$0" "$@" ;;
            *) exec "$t" -e "$0" "$@" ;;
        esac
    done
fi

pause=""; [ -n "${RIPPERDOC_IN_TERMINAL:-}" ] && pause=1
live=""; ask_label=""; open_report=1; gui=""; args=()
while [ $# -gt 0 ]; do
    case "$1" in
        --pause) pause=1 ;;                           # handled here: the exe's own key prompt is unreliable under Wine
        --gui) gui=1; open_report=""; pause="" ;;     # the app window: handled below, nothing to pause for
        --ask-label) ask_label=1 ;;
        --say) shift; echo "${1:-}" ;;
        --live) live=1; open_report="" ;;
        --make-budget-archive|--remove-budget-archive|--json|--help|--out) open_report=""; args+=("$1") ;;
        *) args+=("$1") ;;
    esac
    shift
done

finish() {
    if [ -n "$pause" ]; then echo; read -r -p "Press Enter to close. " _ || true; fi
    exit "$1"
}

if [ ! -f "$exe" ]; then
    echo "VramRipperdoc.exe is missing from $tool, so the tool can't start."
    echo "Keep this Linux folder inside the VRAM RipperDoc folder, next to VramRipperdoc.exe. If the exe is gone,"
    echo "download the mod again from https://www.nexusmods.com/cyberpunk2077/mods/36878 and unpack the whole zip."
    finish 1
fi

# Wine shows the Linux file system as drive Z:.
winpath() { printf 'Z:%s' "${1//\//\\}"; }

steam_roots() {
    local r
    for r in "$HOME/.steam/steam" "$HOME/.steam/root" "$HOME/.local/share/Steam" \
             "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" "$HOME/.var/app/com.valvesoftware.Steam/data/Steam"; do
        [ -d "$r/steamapps" ] && (cd "$r" && pwd -P)
    done | awk '!seen[$0]++'
}

# Every Steam library folder (the SD card on a Steam Deck is one).
steam_libraries() {
    local root vdf
    steam_roots | while IFS= read -r root; do
        echo "$root"
        for vdf in "$root/steamapps/libraryfolders.vdf" "$root/config/libraryfolders.vdf"; do
            [ -f "$vdf" ] && sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$vdf"
        done
    done | awk '!seen[$0]++'
}

# System Wine 8 or newer, else the newest Proton Steam has installed, else any Wine.
find_wine() {
    if [ -n "${RIPPERDOC_WINE:-}" ]; then echo "$RIPPERDOC_WINE"; return; fi
    local w v lib p best=""
    w="$(command -v wine 2>/dev/null)"
    if [ -n "$w" ]; then
        v="$("$w" --version 2>/dev/null)"; v="${v#wine-}"; v="${v%%[!0-9]*}"
        if [ "${v:-0}" -ge 8 ]; then echo "$w"; return; fi
    fi
    best="$({ steam_libraries | while IFS= read -r lib; do printf '%s\n' "$lib"/steamapps/common/Proton*/; done
              steam_roots | while IFS= read -r lib; do printf '%s\n' "$lib"/compatibilitytools.d/*/; done; } |
            while IFS= read -r p; do
                n="$(basename "$p")"
                case "$n" in Proton\ [0-9]*) rank=3 ;; "Proton - Experimental") rank=2 ;; *) rank=1 ;; esac  # stable first
                for b in files/bin/wine64 files/bin/wine dist/bin/wine64 dist/bin/wine; do
                    [ -x "$p$b" ] && { printf '%s\t%s\t%s\n' "$rank" "$n" "$p$b"; break; }
                done
            done | sort -t "$(printf '\t')" -k1,1n -k2,2V | tail -n 1 | cut -f 3)"
    if [ -n "$best" ]; then echo "$best"; return; fi
    [ -n "$w" ] && echo "$w"
}

is_game() { [ -d "$1/archive/pc/content" ]; }

find_game() {
    if [ -n "${RIPPERDOC_GAME:-}" ]; then echo "$RIPPERDOC_GAME"; return; fi
    local d="$tool" lib f c
    while [ -n "$d" ] && [ "$d" != "/" ]; do is_game "$d" && { echo "$d"; return; }; d="$(dirname "$d")"; done  # installed as a mod
    while IFS= read -r lib; do
        is_game "$lib/steamapps/common/Cyberpunk 2077" && { echo "$lib/steamapps/common/Cyberpunk 2077"; return; }
    done < <(steam_libraries)
    # Heroic (GOG, Epic) records where it installed the game.
    for f in "$HOME/.config/heroic/gog_store/installed.json" "$HOME/.config/legendary/installed.json" \
             "$HOME/.var/app/com.heroicgameslauncher.hgl/config/heroic/gog_store/installed.json" \
             "$HOME/.var/app/com.heroicgameslauncher.hgl/config/legendary/installed.json"; do
        [ -f "$f" ] || continue
        while IFS= read -r d; do is_game "$d" && { echo "$d"; return; }; done \
            < <(grep -o '"install_path"[[:space:]]*:[[:space:]]*"[^"]*"' "$f" | sed 's/.*"\([^"]*\)"$/\1/')
    done
    # Lutris, Bottles and hand-made Wine prefixes.
    for d in "$HOME/Games" "$HOME/.var/app/com.usebottles.bottles/data/bottles/bottles" "$HOME/.local/share/bottles/bottles"; do
        [ -d "$d" ] || continue
        while IFS= read -r c; do
            c="$(dirname "$(dirname "$(dirname "$c")")")"
            is_game "$c" && { echo "$c"; return; }
        done < <(find "$d" -maxdepth 9 -type d -path '*/archive/pc/content' 2>/dev/null)
    done
}

# The game's UserSettings.json, inside the Wine/Proton prefix the game runs in.
find_settings() {
    local game="$1" rel="AppData/Local/CD Projekt Red/Cyberpunk 2077/UserSettings.json" f d
    case "$game" in
        */steamapps/common/*)
            f="${game%/steamapps/common/*}/steamapps/compatdata/1091500/pfx/drive_c/users/steamuser/$rel"
            [ -f "$f" ] && { echo "$f"; return; } ;;
    esac
    d="$game"
    while [ -n "$d" ] && [ "$d" != "/" ]; do        # installed inside its prefix (Lutris, Bottles)
        if [ "$(basename "$d")" = "drive_c" ]; then
            for f in "$d"/users/*/"$rel"; do [ -f "$f" ] && { echo "$f"; return; }; done
        fi
        d="$(dirname "$d")"
    done
    for f in "$HOME"/Games/Heroic/Prefixes/*/*/drive_c/users/*/"$rel" "$HOME"/Games/Heroic/Prefixes/*/*/pfx/drive_c/users/*/"$rel"; do
        [ -f "$f" ] && { echo "$f"; return; }
    done
}

# Prints "<VRAM in GB>\t<name>\t<shared>". Shared-memory GPUs (Steam Deck, other APUs) have almost no VRAM of their
# own and borrow system RAM, so they are counted as 8 GB; --vram changes that.
detect_gpu() {
    local line card v best=0 bestcard="" name vendor
    if command -v nvidia-smi >/dev/null 2>&1; then
        line="$(nvidia-smi --query-gpu=memory.total,name --format=csv,noheader,nounits 2>/dev/null | head -n 1)"
        if [ -n "$line" ]; then LC_ALL=C awk -F', *' '{ printf "%.2f\t%s\t\n", $1 / 1024, $2 }' <<<"$line"; return; fi
    fi
    for card in "$sys_root"/class/drm/card[0-9]*/device; do
        [ -r "$card/mem_info_vram_total" ] || continue
        v="$(cat "$card/mem_info_vram_total")"
        [ "$v" -gt "$best" ] && { best=$v; bestcard=$card; }
    done
    [ -n "$bestcard" ] || return
    name=""
    command -v lspci >/dev/null 2>&1 && name="$(lspci -s "$(basename "$(readlink -f "$bestcard")")" 2>/dev/null | sed 's/^.*controller: //; s/ (rev .*)$//')"
    if [ "$best" -lt $((2 << 30)) ]; then
        vendor="$(cat "$sys_root/devices/virtual/dmi/id/board_vendor" 2>/dev/null)"
        [ "$vendor" = "Valve" ] && name="Steam Deck"
        printf '8\t%s (shared memory, counted as 8 GB)\tshared\n' "${name:-Graphics}"
    else
        LC_ALL=C awk -v b="$best" -v n="${name:-Graphics card}" 'BEGIN { printf "%.2f\t%s\t\n", b / 1073741824, n }'
    fi
}

wine="$(find_wine)"
if [ -z "$wine" ]; then
    echo "Wine or Proton wasn't found, and VRAM RipperDoc needs one of them to run on Linux."
    echo "Installing Cyberpunk 2077 through Steam brings Proton along. Otherwise install Wine 8 or newer from your"
    echo "distribution, or point RIPPERDOC_WINE at a wine binary."
    finish 1
fi
wine_env=()
case "$wine" in
    */files/bin/*|*/dist/bin/*)   # Proton's Wine, used outside Steam: point it at its own libraries (for Wine only)
        pdir="$(dirname "$(dirname "$wine")")"
        wine_env=("WINEDLLPATH=$pdir/lib64/wine:$pdir/lib/wine" "LD_LIBRARY_PATH=$pdir/lib64:$pdir/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}") ;;
esac
run_wine() { env "${wine_env[@]}" "$wine" "$@"; }

# A Wine folder just for this tool; the game's own Proton prefix is never touched.
export WINEPREFIX="${RIPPERDOC_PREFIX:-${XDG_DATA_HOME:-$HOME/.local/share}/vram-ripperdoc/wine}"
export WINEDEBUG="${WINEDEBUG:--all}"
export WINEDLLOVERRIDES="mscoree,mshtml=;winemenubuilder.exe=d"   # no Mono/Gecko prompts, no desktop menu entries
if [ ! -f "$WINEPREFIX/system.reg" ]; then
    echo "First run: setting up a Wine folder for RipperDoc (only once, can take a minute)..."
    mkdir -p "$WINEPREFIX"
    run_wine wineboot --init >/dev/null 2>&1
fi

game="$(find_game)"
extra=()
if [ -n "$game" ]; then
    extra+=(--game "$(winpath "$game")")
    settings="$(find_settings "$game")"
    [ -n "$settings" ] && extra+=(--settings "$(winpath "$settings")")
fi
IFS=$'\t' read -r vram gpu shared < <(detect_gpu)
[ -n "${vram:-}" ] && extra+=(--vram "$vram" --gpu "$gpu")
extra+=(--no-open)   # the report is opened below with the Linux browser, not through Wine

# Per-process VRAM from the kernel's DRM fdinfo (AMD, Intel): every GPU client the game's processes hold, counted once.
game_pids() {
    grep -l -a -i 'cyberpunk2077\.exe' "$proc_root"/[0-9]*/cmdline 2>/dev/null | sed 's|.*/\([0-9]*\)/cmdline$|\1|'
}
fdinfo_vram() {
    local files=() pid
    for pid in "$@"; do files+=("$proc_root/$pid"/fdinfo/*); done
    grep -s -H -E '^drm-(client-id|memory-vram|resident-vram|resident-vram0|resident-local0|memory-gtt|resident-gtt):' "${files[@]}" |
        LC_ALL=C awk -F': *' -v shared="${shared:-}" '
            function bytes(v,   n, u) { split(v, a, /[ \t]+/); n = a[1] + 0; u = a[2]
                return u == "KiB" ? n * 1024 : u == "MiB" ? n * 1048576 : u == "GiB" ? n * 1073741824 : n }
            { f = $1; k = $2; sub(/^[ \t]+/, "", $3)
              if (k == "drm-client-id") client[f] = $3
              else if (k ~ /gtt/) { if (shared != "") gtt[f] = bytes($3) }
              else if (bytes($3) > vram[f]) vram[f] = bytes($3) }
            END { for (f in client) used[client[f]] = vram[f] + gtt[f]
                  for (c in used) total += used[c]; printf "%.0f\n", total }'
}
nvidia_vram() {   # MB per process from nvidia-smi pmon, for the given pids
    nvidia-smi pmon -c 1 -s m 2>/dev/null | LC_ALL=C awk -v pids=" $* " '
        !/^#/ && index(pids, " " $2 " ") && $4 ~ /^[0-9]+$/ { mb += $4 } END { printf "%.0f\n", mb * 1048576 }'
}
gpu_used() {
    if command -v nvidia-smi >/dev/null 2>&1; then
        nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits 2>/dev/null | head -n 1 | LC_ALL=C awk '{ printf "%.0f\n", $1 * 1048576 }'
        return
    fi
    local card best=0 v
    for card in "$sys_root"/class/drm/card[0-9]*/device; do
        [ -r "$card/mem_info_vram_used" ] || continue
        v="$(cat "$card/mem_info_vram_used")"
        [ -n "${shared:-}" ] && [ -r "$card/mem_info_gtt_used" ] && v=$((v + $(cat "$card/mem_info_gtt_used")))
        [ "$v" -gt "$best" ] && best=$v
    done
    echo "$best"
}
sample_game() {   # "<game VRAM bytes> <GPU total bytes>", one line per second until the game closes
    local pids g a seen="" zero=0 whole_gpu=""
    while pids="$(game_pids)"; [ -n "$pids" ]; do
        # shellcheck disable=SC2086
        if command -v nvidia-smi >/dev/null 2>&1; then g="$(nvidia_vram $pids)"; else g="$(fdinfo_vram $pids)"; fi
        a="$(gpu_used)"
        # Steam's launch wrappers show up before the game has a GPU context (0 samples are skipped). A whole minute
        # without the game's own number means the driver doesn't report it: measure the whole GPU instead.
        if [ "${g:-0}" -gt 0 ]; then seen=1
        elif [ -z "$seen" ] && [ -z "$whole_gpu" ]; then
            zero=$((zero + 1))
            [ "$zero" -ge 60 ] && { whole_gpu=1; echo "(This driver doesn't report the game's own VRAM; measuring the whole GPU instead.)" >&2; }
        fi
        [ -n "$whole_gpu" ] && g=$a
        echo "${g:-0} ${a:-0}"
        sleep 1
    done
}

# The app window: the exe serves one page on 127.0.0.1 (opened here with the Linux browser, not through Wine) and ends
# by itself a little after the window is closed.
port_answers() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }
random_token() {
    local t
    t="$(od -An -N16 -tx1 /dev/urandom 2>/dev/null | tr -d ' \n')"
    [ "${#t}" -ge 16 ] || t="$(printf '%x%x%x%x' "$RANDOM" "$RANDOM" "$RANDOM" "$RANDOM")$(date +%s%N 2>/dev/null)"
    echo "$t"
}
open_app_window() {   # an app-style window when a Chromium-family browser is installed, else the default browser
    local url="$1" b id
    local opener=(); command -v setsid >/dev/null 2>&1 && opener=(setsid)   # detached: closing the terminal
    for b in chromium chromium-browser google-chrome google-chrome-stable microsoft-edge brave-browser; do
        command -v "$b" >/dev/null 2>&1 || continue
        ("${opener[@]}" "$b" "--app=$url" >/dev/null 2>&1 &)                 # mustn't close the app
        return 0
    done
    if command -v flatpak >/dev/null 2>&1; then
        for id in org.chromium.Chromium com.google.Chrome com.microsoft.Edge com.brave.Browser; do
            flatpak info "$id" >/dev/null 2>&1 || continue
            ("${opener[@]}" flatpak run "$id" "--app=$url" >/dev/null 2>&1 &)
            return 0
        done
    fi
    if command -v xdg-open >/dev/null 2>&1; then
        ("${opener[@]}" xdg-open "$url" >/dev/null 2>&1 &)
        return 0
    fi
    return 1
}
if [ -n "$gui" ]; then
    port=""
    for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20; do
        p=$((47800 + (RANDOM * 32768 + RANDOM) % 1000))
        port_answers "$p" || { port="$p"; break; }                           # something answers there: pick another
    done
    if [ -z "$port" ]; then echo "Couldn't find a free port for the app."; finish 1; fi
    token="$(random_token)"
    url="http://127.0.0.1:$port/?t=$token"
    run_wine "$exe" "${extra[@]}" --gui --gui-port "$port" --gui-token "$token" "${args[@]}" &
    exe_pid=$!
    ready=""
    for _ in $(seq 1 240); do                                                # about 60 seconds
        kill -0 "$exe_pid" 2>/dev/null || break
        if port_answers "$port"; then ready=1; break; fi
        sleep 0.25
    done
    if [ -z "$ready" ]; then
        if kill -0 "$exe_pid" 2>/dev/null; then
            echo "The app didn't start within a minute, so it was stopped."
            kill "$exe_pid" 2>/dev/null
            wait "$exe_pid" 2>/dev/null
            code=1
        else
            wait "$exe_pid" 2>/dev/null; code=$?
            echo "The app closed right after starting (exit code $code), so there is nothing to open."
            [ "$code" -eq 0 ] && code=1
        fi
        echo "Try the numbered files in this folder instead, or run: ./ripperdoc.sh --help"
        finish "$code"
    fi
    echo "Opening the VRAM RipperDoc app..."
    echo "Closing the app window ends the tool."
    if ! open_app_window "$url"; then
        echo "No browser was found to open it. Open this address yourself:"
        echo "$url"
    fi
    wait "$exe_pid"
    code=$?
    finish "$code"
fi

start_marker="$(mktemp)"
if [ -n "$live" ]; then
    label=""
    if [ -n "$ask_label" ]; then read -r -p "Name this measurement (for example: before or after): " label || true; fi
    echo
    echo "Now start the game and load a save. Play for a minute or two, then close the game to finish."
    stopped=""
    trap 'stopped=1; echo' INT
    echo "Waiting for Cyberpunk 2077 to start... (Ctrl+C to stop)"
    until [ -n "$stopped" ] || [ -n "$(game_pids)" ]; do sleep 1; done
    if [ -n "$stopped" ]; then echo "Stopped before the game started, so nothing was measured."; rm -f "$start_marker"; finish 1; fi
    # Ctrl+C now only stops the sampler. The exe runs in its own session so it doesn't get Ctrl+C: it sees the end of
    # the samples and saves the session.
    detach=(); command -v setsid >/dev/null 2>&1 && detach=(setsid)
    sample_game | "${detach[@]}" env "${wine_env[@]}" "$wine" "$exe" "${extra[@]}" --live --samples - --label "${label:-session}" "${args[@]}"
    code=${PIPESTATUS[1]}
    trap - INT
else
    run_wine "$exe" "${extra[@]}" "${args[@]}"
    code=$?
fi

report="$tool/VRAM-RipperDoc-Report.html"
if [ -n "$open_report" ] && [ "$code" -eq 0 ] && [ "$report" -nt "$start_marker" ] && command -v xdg-open >/dev/null 2>&1; then
    opener=(xdg-open); command -v setsid >/dev/null 2>&1 && opener=(setsid xdg-open)   # detached: closing the terminal
    ("${opener[@]}" "$report" >/dev/null 2>&1 &)                                         # mustn't close the browser
fi
rm -f "$start_marker"
finish "$code"
