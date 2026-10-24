#!/usr/bin/env bash
# Runs every automated check: builds once, runs the unit tests, then each check mode of the game, several at a time,
# and prints a pass/fail table with each failure's AUTOTEST line. Exits 1 if anything failed.
#
#   tools/check.sh                 everything (the windowed checks too when there's a display)
#   tools/check.sh --headless      only the checks that run without a window (what CI runs)
#   tools/check.sh -j 2            two at a time (default: half the CPU cores, at least 2)
#   tools/check.sh hanger hazard   only the checks whose names contain one of these words
#
# Godot comes from $GODOT, else the first Godot .NET console build found in the usual places. Logs go to
# check-logs/ (one file per check). Every check fails if the game logged an error on the way (see ErrorWatch).
set -u
cd "$(dirname "$0")/.."

headless_only=0
jobs=$(( $(nproc 2>/dev/null || echo 4) / 2 ))
[ "$jobs" -lt 2 ] && jobs=2
filters=()
while [ $# -gt 0 ]; do
  case "$1" in
    --headless) headless_only=1 ;;
    -j) shift; jobs=$1 ;;
    -j*) jobs=${1#-j} ;;
    *) filters+=("$1") ;;
  esac
  shift
done

if [ -z "${GODOT:-}" ]; then
  for g in ~/godot/*/Godot_v*_mono_linux.x86_64 ~/godot/Godot_v*_mono_linux.x86_64 \
           /c/Users/*/Downloads/Godot_v*_mono_win64/Godot_v*_mono_win64/Godot_v*_mono_win64_console.exe; do
    [ -x "$g" ] && GODOT=$g && break
  done
fi
[ -z "${GODOT:-}" ] && { echo "Set GODOT to the Godot .NET editor binary (the _console.exe on Windows)."; exit 2; }

# A display for the windowed checks: always on Windows and macOS, $DISPLAY or $WAYLAND_DISPLAY on Linux.
display=0
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*|Darwin) display=1 ;;
  *) [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ] && display=1 ;;
esac
[ "$headless_only" = 1 ] && display=0

# name | windowed (0 = headless, 1 = 1920x1080 window, 2 = a window at its own size) | timeout (s) | arguments after --
CHECKS=(
  "errorcheck|0|60|--errorcheck"
  "dailycheck|0|900|--dailycheck=20261023 --seed=1"
  "trainingcheck|0|300|--trainingcheck --seed=1"
  "cardcheck|0|900|--cardcheck --seed=1"
  "autotest|0|900|--autotest"
  "autotest-8|0|900|--autotest --players=8"
  "teams-3v3|0|900|--autotest --players=6 --teams=3"
  "koth|0|900|--autotest --mode=koth"
  "ctf|0|900|--autotest --mode=ctf"
  "headhunter|0|900|--autotest --mode=headhunter"
  "gungame|0|900|--autotest --mode=gungame"
  "survival|0|900|--autotest --mode=survival --couch=2"
  "random-cards|0|900|--autotest --cards=random:5"
  "rematch|0|900|--autotest --rematch --rounds=1"
  "killcamskip|0|900|--autotest --killcamskip"
  "couch|0|900|--autotest --couch=2"
  "overlay|0|900|--autotest --overlaycheck"
  "lava|0|900|--autotest --lavacheck"
  "padcheck|0|300|--padcheck"
  "rebindcheck|0|120|--rebindcheck"
  # The bot checks run on fixed seeds, so they give the same numbers every time; their limits sit above the worst of seeds 1-8.
  "hazardcheck-1|0|900|--hazardcheck --seed=1"
  "hazardcheck-2|0|900|--hazardcheck --seed=2"
  "hazardcheck-3|0|900|--hazardcheck --seed=3"
  "vantagecheck-1|0|600|--vantagecheck --seed=1"
  "vantagecheck-2|0|600|--vantagecheck --seed=2"
  "vantagecheck-3|0|600|--vantagecheck --seed=3"
  "hangercheck-1|0|600|--hangercheck --seed=1"
  "hangercheck-2|0|600|--hangercheck --seed=2"
  "hangercheck-3|0|600|--hangercheck --seed=3"
  "driftcheck-1|0|900|--driftcheck --seed=1"
  "driftcheck-2|0|900|--driftcheck --seed=2"
  "driftcheck-3|0|900|--driftcheck --seed=3"
  "net|0|900|NET"
  "net-rejoin|0|900|NET --dropat=1"
  "editorcheck|1|600|--editorcheck"
  "lobbycheck|1|600|--lobbycheck"
  "clipcheck|1|600|--clipcheck"
  "windowcheck|2|300|--windowcheck"
)

logs=check-logs
rm -rf "$logs"
mkdir -p "$logs"

echo "Building..."
if ! dotnet build godot/Brawler.csproj -v q -nologo > "$logs/build.log" 2>&1 || ! grep -q " 0 Error(s)" "$logs/build.log"; then
  grep -E " error " "$logs/build.log" | sort -u | head -20
  echo "BUILD FAILED (see $logs/build.log)"
  exit 1
fi
"$GODOT" --headless --path godot --import > "$logs/import.log" 2>&1 || true

wanted() {
  [ ${#filters[@]} -eq 0 ] && return 0
  local f
  for f in "${filters[@]}"; do [[ "$1" == *"$f"* ]] && return 0; done
  return 1
}

# One check: its log, and a result file holding "PASS|FAIL|<the AUTOTEST line or why>".
run_check() {
  local name=$1 windowed=$2 limit=$3 args=$4
  local log=$logs/$name.log res=$logs/$name.result
  local mode=(--headless)
  [ "$windowed" = 1 ] && mode=(--resolution 1920x1080)
  [ "$windowed" = 2 ] && mode=()
  if [[ "$args" == NET* ]]; then
    # Networked: a host and a joining client on their own port, both must pass.
    local port=$(( 24700 + RANDOM % 200 )) extra=${args#NET}
    timeout "$limit" "$GODOT" --headless --path godot --fixed-fps 60 -- --autohost=$port > "$log.host" 2>&1 &
    local host=$!
    sleep 3
    timeout "$limit" "$GODOT" --headless --path godot --fixed-fps 60 -- --autojoin=127.0.0.1:$port $extra > "$log" 2>&1
    local client=$?
    wait $host
    local hostcode=$?
    local hl cl
    hl=$(grep -a "AUTOTEST" "$log.host" | tail -1)
    cl=$(grep -a "AUTOTEST" "$log" | tail -1)
    if [ $client -eq 0 ] && [ $hostcode -eq 0 ] && [[ "$hl" == *"AUTOTEST PASS"* ]] && [[ "$cl" == *"AUTOTEST PASS"* ]]; then
      echo "PASS|client: ${cl#*AUTOTEST PASS: } / host: ${hl#*AUTOTEST PASS: }" > "$res"
    else
      echo "FAIL|client (exit $client): ${cl:-no result} / host (exit $hostcode): ${hl:-no result}" > "$res"
    fi
    return
  fi
  local fixed=(--fixed-fps 60)
  [ "$windowed" != 0 ] && fixed=()
  # shellcheck disable=SC2086
  timeout "$limit" "$GODOT" "${mode[@]}" --path godot "${fixed[@]}" -- $args > "$log" 2>&1
  local code=$?
  local line
  line=$(grep -a "AUTOTEST \(PASS\|FAIL\)" "$log" | tail -1)
  if [ $code -eq 0 ] && [[ "$line" == *"AUTOTEST PASS"* ]]; then
    echo "PASS|${line#*AUTOTEST PASS: }" > "$res"
  elif [ $code -eq 124 ]; then
    echo "FAIL|timed out after ${limit}s ${line:+(last: ${line#*AUTOTEST })}" > "$res"
  else
    echo "FAIL|exit $code: ${line:-no AUTOTEST line}" > "$res"
  fi
}

echo "Unit tests..."
if (cd tests && dotnet run -c Release > "../$logs/unit.log" 2>&1); then
  echo "PASS|$(tail -1 "$logs/unit.log")" > "$logs/unit.result"
else
  echo "FAIL|$(grep -a '^FAIL' "$logs/unit.log" | head -3 | tr '\n' ' ')" > "$logs/unit.result"
fi
names=(unit)

skipped=()
for spec in "${CHECKS[@]}"; do
  IFS='|' read -r name windowed limit args <<< "$spec"
  wanted "$name" || continue
  if [ "$windowed" != 0 ] && [ "$display" = 0 ]; then skipped+=("$name"); continue; fi
  names+=("$name")
  while [ "$(jobs -rp | wc -l)" -ge "$jobs" ]; do sleep 1; done
  echo "  $name"
  run_check "$name" "$windowed" "$limit" "$args" &
done
wait

failed=0
printf '\n%-14s %-5s %s\n' CHECK RESULT DETAIL
for name in "${names[@]}"; do
  IFS='|' read -r result detail < "$logs/$name.result"
  [ "$result" = PASS ] || failed=$((failed + 1))
  [ ${#detail} -gt 160 ] && detail="${detail:0:157}..."
  printf '%-14s %-5s %s\n' "$name" "$result" "$detail"
done
[ ${#skipped[@]} -gt 0 ] && echo "skipped (no display): ${skipped[*]}"
echo
if [ $failed -gt 0 ]; then echo "$failed of ${#names[@]} FAILED (logs in $logs/)"; exit 1; fi
echo "all ${#names[@]} passed"
