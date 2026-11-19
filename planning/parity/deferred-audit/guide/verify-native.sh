#!/usr/bin/env bash
# After a Debug build, run from the worktree with Godot Mono, Xvfb, Xdotool and ImageMagick.
set -euo pipefail

script=$(realpath "$0")
root=$(cd "$(dirname "$script")/../../../.." && pwd)
cd "$root"
. "$root/tools/lib/find-godot.sh"
godot=$(find_godot "$root") || { godot_not_found "$root"; exit 1; }

if [[ ${1:-} != --inside-xvfb ]]; then
    run=$(mktemp -d "${TMPDIR:-/tmp}/vortex-guide.XXXXXX")
    echo "Native Guide captures: $run"
    exec xvfb-run -a -s '-screen 0 1120x720x24' bash "$script" --inside-xvfb "$run"
fi

run=$2
pid=
trap 'if [[ -n $pid ]]; then kill "$pid" 2>/dev/null || true; fi' EXIT
export LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=3

launch() {
    local language=$1
    mkdir -p "$run/$language"/{profile,xdg}
    printf 'seta prvm_language "%s"\n' "$language" >"$run/$language/profile/config.cfg"
    VORTEX_USERDIR="$run/$language/profile" XDG_DATA_HOME="$run/$language/xdg" \
        XDG_CONFIG_HOME="$run/$language/xdg" XDG_CACHE_HOME="$run/$language/xdg" \
        "$godot" --path "$root" --rendering-method gl_compatibility --render-thread safe \
        --resolution 1100x700 --audio-driver Dummy --menu-screen media:guide --no-save-config \
        --cvar cl_frameprofiler -1 >"$run/$language/client.log" 2>&1 &
    pid=$!
    window=$(timeout 30 xdotool search --sync --onlyvisible --pid "$pid" --name '^VortexArena' | head -1)
    for ((attempt=0; attempt<120; attempt++)); do
        if grep -Fq 'playerstats_update' "$run/$language/client.log"; then break; fi
        sleep .25
    done
    grep -Fq 'playerstats_update' "$run/$language/client.log"
    xdotool windowfocus "$window"
    sleep .8
    frames="$run/$language"
}

key() { xdotool key --clearmodifiers --delay 130 "$@"; sleep .25; }
click() { xdotool mousemove --window "$window" "$1" "$2"; sleep .15; xdotool click 1; sleep .25; }
type() { xdotool type --clearmodifiers --delay 85 "$1"; sleep .25; }
shot() {
    xdotool mousemove --window "$window" 10 20
    sleep .35
    import -window "$window" "$frames/$1.png"
}

assert_crop() {
    local relation=$1 crop=$2 before=$3 after=$4 difference
    convert "$frames/$before.png" -crop "$crop" +repage "$run/crop-a.png"
    convert "$frames/$after.png" -crop "$crop" +repage "$run/crop-b.png"
    difference=$(compare -metric AE "$run/crop-a.png" "$run/crop-b.png" null: 2>&1) || [[ $? == 1 ]]
    if [[ $relation == same && $difference != 0 || $relation == different && $difference == 0 ]]; then
        echo "$before and $after should be $relation in $crop ($difference differing pixels)" >&2
        exit 1
    fi
}

description=400x470+605+106
entries=250x515+312+100

launch en
shot 01-first-open
click 400 164
shot 02-community
key ctrl+f
type COM
shot 03-filtered
type qqq
key Down Up
shot 04-empty
key BackSpace BackSpace BackSpace
shot 04b-relaxed
type qqq
key ctrl+Delete
shot 05-ctrl-delete
type XONSTAT
shot 06-other-match
type qqq
shot 07-other-match-empty
key BackSpace BackSpace BackSpace
shot 08-other-match-returned
key ctrl+Delete
shot 08b-prior-selection
type o
key Down
shot 09-filter-down
key ctrl+Delete
shot 09b-keyboard-restored
key Return Up
shot 10-entry-up
key ctrl+f
type qqq
key Return
key ctrl+u
shot 11-ctrl-u
key ctrl+f
type qqq
click 575 638
shot 11b-clear-button
key ctrl+f
type qqq
click 150 164
shot 12-first-movement
click 400 122
key Down
shot 13-bunny-hopping
click 150 164
key Up
shot 14-intro-restored
key Down
shot 15-movement-restored
key Up
click 400 164
key ctrl+f
type XONSTAT
shot 16-fallback-before-click
click 400 122
key ctrl+u
shot 17-click-restored
click 150 164
key Up
shot 18-click-topic-restored

assert_crop different "$description" 01-first-open 02-community
assert_crop same "$description" 02-community 03-filtered
assert_crop different "$description" 03-filtered 04-empty
assert_crop same "$description" 02-community 04b-relaxed
assert_crop same "$entries" 03-filtered 04b-relaxed
assert_crop same "$description" 02-community 05-ctrl-delete
assert_crop same "$entries" 02-community 05-ctrl-delete
assert_crop different "$description" 04-empty 06-other-match
assert_crop different "$description" 02-community 06-other-match
assert_crop same "$description" 04-empty 07-other-match-empty
assert_crop same "$entries" 04-empty 07-other-match-empty
assert_crop same "$description" 06-other-match 08-other-match-returned
assert_crop same "$entries" 06-other-match 08-other-match-returned
assert_crop same "$description" 02-community 08b-prior-selection
assert_crop same "$entries" 02-community 08b-prior-selection
assert_crop different "$description" 02-community 09-filter-down
assert_crop same "$description" 09-filter-down 09b-keyboard-restored
assert_crop same "$description" 02-community 10-entry-up
assert_crop same "$entries" 02-community 11-ctrl-u
assert_crop same "$description" 02-community 11-ctrl-u
assert_crop same "$entries" 02-community 11b-clear-button
assert_crop same "$description" 02-community 11b-clear-button
assert_crop different "$description" 12-first-movement 13-bunny-hopping
assert_crop same "$entries" 02-community 14-intro-restored
assert_crop same "$description" 02-community 14-intro-restored
assert_crop same "$entries" 13-bunny-hopping 15-movement-restored
assert_crop same "$description" 13-bunny-hopping 15-movement-restored
assert_crop same "$entries" 06-other-match 16-fallback-before-click
assert_crop same "$description" 06-other-match 17-click-restored
assert_crop different "$entries" 16-fallback-before-click 17-click-restored
assert_crop same "$description" 17-click-restored 18-click-topic-restored
assert_crop same "$entries" 17-click-restored 18-click-topic-restored

for ((topic=0; topic<13; topic++)); do
    click 150 "$((122 + 42 * topic))"
    click 400 122
    key Home
    printf -v name 'topic-%02d' "$((topic+1))"
    shot "$name-first"
    key End
    shot "$name-last"
    if [[ $topic == 2 ]]; then
        key Up
        shot "$name-penultimate"
    fi
done
kill "$pid"
wait "$pid" || true
pid=

launch fr
grep -Fq "language 'fr'" "$frames/client.log"
shot 01-first-open
click 400 205
shot 02-getting-good
key ctrl+f
type LIORE
shot 03-localized-filter
type qqq
shot 04-empty
key ctrl+Delete
shot 05-localized-restored

assert_crop different "$description" 01-first-open 02-getting-good
assert_crop same "$description" 02-getting-good 03-localized-filter
assert_crop different "$description" 03-localized-filter 04-empty
assert_crop same "$description" 02-getting-good 05-localized-restored
assert_crop same "$entries" 02-getting-good 05-localized-restored
echo "Native Guide interaction verified. Compare the topic frames to the pinned Guide captures."
