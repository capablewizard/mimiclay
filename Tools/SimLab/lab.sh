#!/bin/bash
# SimLab — the networked-sim collision lab harness (see Tools/SimLab/README.md). Run from Git Bash:
#   Tools/SimLab/lab.sh open                     editor: stop play, open scenes/tests/simlab.scene, play (the HOST)
#   Tools/SimLab/lab.sh cast                     (re)launch the 3 test clients (A, B = player props, H = hunter) on the current build
#   Tools/SimLab/lab.sh lag N                    net_fakelag N on the 3 clients (0 ≈ 115 ms sim round trip, 40 ≈ 210, 90 ≈ 310)
#   Tools/SimLab/lab.sh run [-n N] [-d] SCEN...  run scenarios (N times each; -d = frame dumps); prints + logs to Tools/SimLab/out/
#   Tools/SimLab/lab.sh run all                  every scenario (see SimLab.Scenarios)
#   Tools/SimLab/lab.sh sum [LOG]                summarise a log (default: the latest) against the host's truth
#   Tools/SimLab/lab.sh close                    close the 3 test clients
#   Tools/SimLab/lab.sh full [-n N] [--pause]    all of it: open, cast, no lag, every scenario N times, summary, close
#   Tools/SimLab/lab.sh loop [--pause]           every scenario over and over until out/STOP exists (Stop Continuous Run)
#   Tools/SimLab/lab.sh watch                    launch an OBSERVER client whose free cam flies to each scenario as it runs
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"; HERE_W="$(cygpath -m "$HERE")"
OUT="$HERE/out"; OUT_W="$HERE_W/out"; mkdir -p "$OUT"
STATE="$OUT/state.txt"
SBOX="${SBOX_DIR:-D:/SteamLibrary/steamapps/common/sbox}"; CLI="$SBOX/bin/managed/sbox-cli.exe"
ALL="loose static push headon held swing throw jump lying wall ledge pushwall twopush throwplayer climb corner inner hcorner"

host_pid() { "$CLI" --list 2>&1 | tr -d '\r' | awk '/"pid"/{gsub(/[^0-9]/,"",$0); pid=$0} /"kind": "editor"/{print pid; exit}'; }
# run C# in an instance: cs PID TIMEOUT 'code' → the script's return value (all lines)
cs() { local f="$OUT/_cs_$1_$RANDOM.cs"; printf '%s\n' "$3" > "$f"; "$CLI" --connect $1 --request-timeout $2 --call run_csharp "{\"path\":\"$(cygpath -m "$f")\"}" 2>&1 | tr -d '\r' | grep -v '^$'; rm -f "$f"; }
load_state() { [ -f "$STATE" ] || { echo "no cast yet: run '$0 cast'"; exit 1; }; source "$STATE"; HOST=$(host_pid); }
# GAME clients only (kind "game"), never the editor: listing pids of every kind once picked up the editor as a client (it
# was missing from the list while it hotloaded), and the next cast or close would have force-killed the editor.
game_pids() { "$CLI" --list 2>&1 | tr -d '\r' | awk '/"pid"/{gsub(/[^0-9]/,"",$0); pid=$0} /"kind": "game"/{print pid}'; }
# Kill a test client: only if that pid is a running sbox.exe (a game client) - never the editor (sbox-dev.exe) or anything else.
kill_client() { [ -n "${1:-}" ] && tasklist //FI "PID eq $1" //FI "IMAGENAME eq sbox.exe" 2>/dev/null | grep -q " $1 " && taskkill //PID $1 //F > /dev/null 2>&1; }
OBSF="$OUT/observer.txt"   # the observer client's pid (lab.sh watch), if one is running
obs_pid() { [ -f "$OBSF" ] && cat "$OBSF"; }

case "${1:-}" in
open)
	HOST=$(host_pid); [ -z "$HOST" ] && { echo "no editor running"; exit 1; }
	"$CLI" --connect $HOST --request-timeout 60 --call play_stop '{}' > /dev/null 2>&1
	"$CLI" --connect $HOST --request-timeout 60 --call open_scene '{"path":"scenes/tests/simlab.scene"}' 2>&1 | tr -d '\r' | grep -E '"Active"'
	"$CLI" --connect $HOST --request-timeout 60 --call play_start '{}' > /dev/null 2>&1
	for i in $(seq 1 30); do sleep 3; st=$(cs $HOST 20 'return $"scene {Game.ActiveScene?.Name} net {Networking.IsActive} host {Networking.IsHost} stations {Game.ActiveScene?.GetAllObjects(true).Count(g => g.Name.StartsWith("SimLab."))}";'); echo "$st" | grep -q "net True" && break; done
	echo "host: $st" ;;
cast)
	[ -f "$STATE" ] && { source "$STATE"; for p in ${A:-} ${B:-} ${H:-}; do kill_client $p; done; sleep 3; }
	before=" $(game_pids | tr '\n' ' ') "
	for id in 97 98 99; do powershell -NoProfile -Command "Start-Process -FilePath '$SBOX/sbox.exe' -ArgumentList '-joinlocal','+instanceid','$id','-sw','-720','-mcp' -WorkingDirectory '$SBOX'"; sleep 2; done
	new=""; for i in $(seq 1 80); do sleep 3; new=""; for p in $(game_pids); do case "$before" in *" $p "*) ;; *) new="$new $p";; esac; done; [ $(echo $new | wc -w) -ge 3 ] && break; done
	set -- $new; A=$1; B=$2; H=$3
	for i in $(seq 1 80); do ok=0; for p in $A $B $H; do cs $p 15 'return $"hunters {Game.ActiveScene?.GetAllComponents<Mimiclay.HunterController>().Count()} net {Networking.IsActive}";' | grep -q "hunters [1-9] net True" && ok=$((ok+1)); done; [ $ok -eq 3 ] && break; sleep 5; done
	sleep 5
	# A and B are recorded by CONNECTION ID (NA / NB; display names DA / DB only for messages): names aren't unique - two
	# test clients once both came up as "Ned" and the lab found the same body for both.
	swap='var cm = Game.ActiveScene.GetAllComponents<Mimiclay.CreativeManager>().FirstOrDefault(); if (cm == null) return "no CreativeManager"; cm.RequestSwap(0f); await GameTask.Delay(3000); return Connection.Local.Id + " " + Connection.Local.DisplayName;'
	RA=$(cs $A 30 "$swap" | tail -1); RB=$(cs $B 30 "$swap" | tail -1)
	NA=${RA%% *}; DA=${RA#* }; NB=${RB%% *}; DB=${RB#* }
	for r in "$NA" "$NB"; do [[ "$r" =~ ^[0-9a-f-]{36}$ ]] || { echo "cast failed: a client answered '$RA' / '$RB'"; exit 1; }; done
	printf 'A=%s\nB=%s\nH=%s\nNA=%s\nNB=%s\nDA="%s"\nDB="%s"\n' $A $B $H "$NA" "$NB" "$DA" "$DB" > "$STATE"
	echo "cast: A=$A ($DA) B=$B ($DB) H=$H" ;;
lag)
	load_state
	for p in $A $B $H; do
		"$CLI" --connect $p --request-timeout 20 --call instance_console_command "{\"command\":\"net_fakelag $2\"}" > /dev/null 2>&1
		cs $p 20 "return Mimiclay.SimLab.NoteFakeLag(\"$2\");" > /dev/null   # (shown on screen: game code can't read net_fakelag)
	done
	sleep 10; RTT=$(cs $A 20 'return $"{Game.ActiveScene.GetSystem<Mimiclay.Sim.SimWorld>().RoundTripMs:0}";' | tail -1)
	echo "fakelag $2 set; measured sim round trip now $RTT ms (expect roughly $(( 2 * $2 + 100 )) ms: ~100 ms with no lag here)"
	# net_fakelag adds about 2 x N to the round trip. In one editor session it silently stopped applying (client or host side):
	# a round trip that doesn't move means the test is NOT running at the lag asked for.
	[ "$2" -gt 0 ] && [ "${RTT:-0}" -lt $(( 2 * $2 + 50 )) ] && echo "!! the round trip didn't rise - net_fakelag is not taking effect in this session" ;;
run)
	shift; load_state; N=1; DUMP=false
	while [ $# -gt 0 ]; do case "$1" in -n) N=$2; shift 2;; -d) DUMP=true; shift;; *) break;; esac; done
	[ "${1:-}" = all ] && set -- $ALL
	LOG="${LABLOG:-$OUT/run_$(date +%Y%m%d_%H%M%S).log}"; echo "# lab run $(date) N=$N dump=$DUMP: $*" > "$LOG"
	for SCEN in "$@"; do for r in $(seq 1 $N); do
		echo "##### $SCEN" | tee -a "$LOG"
		OB=$(obs_pid); [ -n "$OB" ] && ( cs $OB 20 "return await Mimiclay.SimLab.Observe(\"$SCEN\");" > /dev/null ) &   # (the observer flies over)
		echo "  $(cs $H 30 "return await Mimiclay.SimLab.HunterPrepare(\"$( case $SCEN in held|swing|throw|throwplayer) echo park;; *) echo $SCEN;; esac )\", \"$NB\");" | tail -1)" | tee -a "$LOG"
		echo "  $(cs $HOST 30 "return await Mimiclay.SimLab.Setup(\"$SCEN\", \"$NA\", \"$NB\");" | tail -1)" | tee -a "$LOG"
		case $SCEN in held|swing|throw|throwplayer) echo "  $(cs $H 30 "return await Mimiclay.SimLab.HunterPrepare(\"$SCEN\", \"$NB\");" | tail -1)" | tee -a "$LOG"; cs $HOST 30 "return await Mimiclay.SimLab.Setup(\"$SCEN\", \"$NA\", \"$NB\", 2);" > /dev/null;; esac
		sleep 1.5
		( cs $HOST 90 "return await Mimiclay.SimLab.Run(\"$SCEN\", \"HOST\", \"$NA\", \"$NB\", 9f, $DUMP);" > "$OUT/_o_HOST.txt" ) & sleep 0.5
		( cs $A 90 "return await Mimiclay.SimLab.Run(\"$SCEN\", \"A\", \"$NA\", \"$NB\", 6f, $DUMP);" > "$OUT/_o_A.txt" ) &
		( cs $B 90 "return await Mimiclay.SimLab.Run(\"$SCEN\", \"B\", \"$NA\", \"$NB\", 6f, $DUMP);" > "$OUT/_o_B.txt" ) &
		( cs $H 90 "return await Mimiclay.SimLab.Run(\"$SCEN\", \"H\", \"$NA\", \"$NB\", 6f, $DUMP);" > "$OUT/_o_H.txt" ) &
		wait
		for R in A B H HOST; do cat "$OUT/_o_$R.txt" >> "$LOG"; head -1 "$OUT/_o_$R.txt" | cut -c1-260; done
	done; done
	echo "log: $LOG" ;;
sum)
	LOG="${2:-$(ls -t "$OUT"/run_*.log 2>/dev/null | head -1)}"; awk -f "$HERE/lab_sum.awk" "$LOG" ;;
close)
	OB=$(obs_pid); [ -n "$OB" ] && kill_client $OB; rm -f "$OBSF"
	load_state; for p in $A $B $H; do kill_client $p; done; rm -f "$STATE"; echo "closed" ;;
watch)
	# An OBSERVER: a fourth client (not an actor; nothing it sees is recorded) whose free cam - the creative ghost - flies
	# to each scenario's station as the run reaches it. Start it any time, even during a run; 'close' closes it too.
	OB=$(obs_pid); [ -n "$OB" ] && { kill_client $OB; sleep 2; }
	before=" $(game_pids | tr '\n' ' ') "
	powershell -NoProfile -Command "Start-Process -FilePath '$SBOX/sbox.exe' -ArgumentList '-joinlocal','+instanceid','96','-sw','-720','-mcp' -WorkingDirectory '$SBOX'"
	OB=""; for i in $(seq 1 80); do sleep 3; for p in $(game_pids); do case "$before" in *" $p "*) ;; *) OB=$p;; esac; done; [ -n "$OB" ] && break; done
	[ -z "$OB" ] && { echo "observer never started"; exit 1; }
	for i in $(seq 1 80); do cs $OB 15 'return $"hunters {Game.ActiveScene?.GetAllComponents<Mimiclay.HunterController>().Count()} net {Networking.IsActive}";' | grep -q "hunters [1-9] net True" && break; sleep 5; done
	echo $OB > "$OBSF"
	echo "observer $OB: $(cs $OB 20 'return await Mimiclay.SimLab.Observe("loose");' | tail -1)" ;;
full)
	# All of it in one go (the editor's Mimiclay > SimLab > Run Full Test uses this): open the lab, cast, no lag, every
	# scenario N times, the summary saved to out/summary_<time>.txt and out/summary_latest.txt, clients closed.
	shift; N=1; PAUSE=false
	while [ $# -gt 0 ]; do case "$1" in -n) N=$2; shift 2;; --pause) PAUSE=true; shift;; *) shift;; esac; done
	T0=$(date +%s)
	echo "== SimLab full test: every scenario x$N (about $(( (14 * N * 19 + 150) / 60 )) min). Close this window to abort, then run: Tools/SimLab/lab.sh close =="
	"$0" open || { $PAUSE && read -p "Press Enter to close"; exit 1; }
	"$0" cast
	"$0" lag 0
	"$0" run -n $N all
	SUM="$OUT/summary_$(date +%Y%m%d_%H%M%S).txt"
	"$0" sum | tee "$SUM"; cp "$SUM" "$OUT/summary_latest.txt"
	"$0" close
	echo "== done in $(( ($(date +%s) - T0) / 60 )) min. Summary: $SUM =="
	$PAUSE && read -p "Press Enter to close this window" ;;
loop)
	# Continuous: every scenario over and over (for watching, or soaking) until out/STOP exists — the editor's
	# Mimiclay > SimLab > Stop Continuous Run writes it; it's checked between scenarios. out/summary_latest.txt is the
	# running summary over every cycle so far (rewritten after each scenario); clients are closed at the end.
	shift; PAUSE=false; [ "${1:-}" = --pause ] && PAUSE=true
	rm -f "$OUT/STOP"; T0=$(date +%s); cycle=0
	LOOPLOG="$OUT/loop_$(date +%Y%m%d_%H%M%S).log"; echo "# lab loop $(date)" > "$LOOPLOG"
	echo "== SimLab continuous run: every scenario, over and over. Stop: Mimiclay > SimLab > Stop Continuous Run (finishes the current scenario). =="
	"$0" open || { $PAUSE && read -p "Press Enter to close"; exit 1; }
	"$0" cast
	"$0" lag 0
	FAILS=0
	while [ ! -f "$OUT/STOP" ]; do
		cycle=$((cycle + 1)); echo "== cycle $cycle =="
		for SCEN in $ALL; do
			[ -f "$OUT/STOP" ] && break
			SL="$OUT/_scenario.log"; rm -f "$SL"
			LABLOG="$SL" "$0" run "$SCEN" | grep -E "^#####|^HOST" | cut -c1-200
			if grep -q "^HOST" "$SL" 2>/dev/null; then
				tail -n +2 "$SL" >> "$LOOPLOG"; FAILS=0
				awk -f "$HERE/lab_sum.awk" "$LOOPLOG" > "$OUT/summary_latest.txt"
			else
				FAILS=$((FAILS + 1)); echo "!! $SCEN produced no host line (failure $FAILS in a row) - see $SL"
				[ $FAILS -ge 3 ] && { echo "!! 3 failures in a row - stopping"; touch "$OUT/STOP"; }
			fi
		done
	done
	rm -f "$OUT/STOP"
	"$0" close
	cat "$OUT/summary_latest.txt"
	echo "== stopped after $cycle cycle(s), $(( ($(date +%s) - T0) / 60 )) min. Summary: $OUT/summary_latest.txt  log: $LOOPLOG =="
	$PAUSE && read -p "Press Enter to close this window" ;;
*)
	sed -n '2,12p' "$0" ;;
esac
