#!/usr/bin/env bash
# Launches Cities: Skylines II through Steam straight into a save, with the SignBuilder spike
# harness armed, and waits for the game to quit on its own.
#
#   scripts/run-spike.sh [mode] [save-guid]
#     mode       "full" (default) runs every spike test; "smoke" only loads, screenshots and quits;
#                "phase1" runs the Phase 1 batch; "e2e" the full end-to-end tests; "led-e2e" LED glow by day and night; "ui-e2e" only the designer
#                UI tests (a couple of minutes); "debug" bakes one sign and idles with the UI
#                DevTools on localhost:9444 (create spike/done.flag to quit early).
#     save-guid  SaveGameMetadata guid to load (-startGame=...); omit to continue the last save.
#
# Needs the Steam launch option `"<game dir>\Cities2.exe" %command%` so the Paradox launcher is
# skipped. runOnce.txt is read once by the game at boot, appended to its command line and deleted.
set -euo pipefail

mode="${1:-full}"
save="${2:-}"
user_dir="$LOCALAPPDATA/../LocalLow/Colossal Order/Cities Skylines II"

if tasklist | grep -qi cities2.exe; then
  echo "Cities2.exe is already running; close it first." >&2
  exit 1
fi

if [[ ! -d "$user_dir/Mods/SignBuilder/Resources/Spike" ]]; then
  echo "The installed Sign Builder is a release build; build it with -p:SignBuilderTests=true first." >&2
  exit 1
fi

mkdir -p "$user_dir/ModsData/SignBuilder"
printf '%s' "$mode" > "$user_dir/ModsData/SignBuilder/spike.flag"
extra=""
[[ "$mode" == "debug" || "$mode" == "uitest" || "$mode" == *e2e* ]] && extra=" -uiDeveloperMode"
if [[ -n "$save" ]]; then
  printf -- '-startGame=%s -noSplash%s' "$save" "$extra" > "$user_dir/runOnce.txt"
else
  printf -- '-continuelastsave -noSplash%s' "$extra" > "$user_dir/runOnce.txt"
fi

cmd.exe //c start "" "steam://rungameid/949230"
echo "Launched (mode=$mode, save=${save:-last}); waiting for the game to quit..."

# Steam can hold the launch (e.g. a cloud save conflict to confirm): wait up to 5 minutes for the game to start.
started=""
for _ in $(seq 1 60); do
  if tasklist | grep -qi cities2.exe; then started=1; break; fi
  sleep 5
done
if [[ -z "$started" ]]; then
  echo "The game did not start within 5 minutes (is Steam waiting for something?)." >&2
  exit 1
fi

# Hard stop: whatever happens in game, the process is killed after 10 minutes so it is never left running.
for _ in $(seq 1 120); do
  tasklist | grep -qi cities2.exe || break
  sleep 5
done
if tasklist | grep -qi cities2.exe; then
  echo "Still running after 10 minutes: killing Cities2.exe."
  taskkill //IM Cities2.exe //F >/dev/null 2>&1 || true
fi
echo "Game exited. Results: $user_dir/ModsData/SignBuilder/spike"
