# Summarise a SimLab run log (Tools/SimLab/lab.sh sum). Per scenario and screen (A, B, H), against the HOST's line of the
# same run (the truth):
#   depth+  = the screen's deepest drawn box overlap of the scenario pair minus the host's (positive = drawn deeper)
#   centre+ = the host's closest centre distance minus the screen's (positive = drawn closer)
#   vgap+   = landings: the host's lowest vertical gap while stacked minus the screen's (positive = drawn sunk in)
#   dv      = the worst frame-to-frame velocity jump of any watched body on that screen (u/s); blips = frames over 1500 u/s
#   r-a     = throws: drawn reaction of the target minus drawn arrival of the projectile (ms; negative = hit before it arrives) / the drawn box gap between them at that reaction (u; negative = still apart)
# A run whose host recording missed the motion (host A travel < 80% of A's own) is dropped as invalid.
function num(s, key,   r) { if (match(s, key " -?[0-9.]+")) { r = substr(s, RSTART + length(key) + 1, RLENGTH - length(key) - 1); return r + 0 } return "" }
/^##### / { scen = $2; run[scen]++; key = scen SUBSEP run[scen]; order[scen] = order[scen] ? order[scen] : ++nscen; names[nscen] = names[nscen] ? names[nscen] : scen; next }
/^    jump: / { if (njd[scen, role] < 6) { njd[scen, role]++; jd[scen, role] = jd[scen, role] "\n      " role ": " substr($0, 11) }; next }
/^(A|B|H|HOST) +\|/ {
	role = $1; line = $0
	if (match(line, /jumps [0-9]+ \(setup [0-9]+\)/)) { t = substr(line, RSTART + 6, RLENGTH - 6); split(t, jp, " "); js[scen, role] += jp[1] + 0; jset[scen, role] += substr(jp[3], 1, length(jp[3]) - 1) + 0; hasj[scen] = 1 }
	if (match(line, /climb [AH] [0-9]+% faces [0-9]+ \[[^\]]*\] maxz -?[0-9]+/)) { t = substr(line, RSTART + 8, RLENGTH - 8); sub(/ faces /, " ", t); cl[scen, role] = cl[scen, role] (cl[scen, role] != "" ? " / " : "") t; hascl[scen] = 1 }
	if (match(line, /\| end L [^|]*/)) { t = substr(line, RSTART + 2, RLENGTH - 2); sub(/ +$/, "", t); ge[scen, role] = ge[scen, role] (ge[scen, role] != "" ? " / " : "") t; hasge[scen] = 1 }
	c[key, role] = num(line, "centre"); d[key, role] = num(line, "depth"); v[key, role] = num(line, "vgap")
	ra[key, role] = num(line, "react-arrive"); ga[key, role] = num(line, "gap@react")
	if (match(line, / A mv +[0-9]+/)) { t = substr(line, RSTART, RLENGTH); gsub(/[^0-9]/, "", t); mv[key, role] = t + 0 }
	if (match(line, / H mv +[0-9]+/)) { t = substr(line, RSTART, RLENGTH); gsub(/[^0-9]/, "", t); mvh[key, role] = t + 0 }
	if (match(line, / L mv +[0-9]+/)) { t = substr(line, RSTART, RLENGTH); gsub(/[^0-9]/, "", t); mvl[key, role] = t + 0 }
	mx = 0; nb = 0; s = line
	while (match(s, /max +[0-9]+@/)) { t = substr(s, RSTART, RLENGTH); gsub(/[^0-9]/, "", t); if (t + 0 > mx) mx = t + 0; s = substr(s, RSTART + RLENGTH) }
	s = line; while (match(s, />1500:[0-9]+/)) { t = substr(s, RSTART + 6, RLENGTH - 6); nb += t; s = substr(s, RSTART + RLENGTH) }
	if (mx > dv[scen, role]) dv[scen, role] = mx; bl[scen, role] += nb
	if (role != "HOST") next
	if (mv[key, "A"] > 100 && mv[key, "HOST"] < 0.8 * mv[key, "A"]) { bad[scen]++; next }
	# the hunter scenarios: the same test on the HUNTER's travel (A waits in the park there, so a host that missed the motion
	# went unnoticed: one hpush run at fakelag 40 read 116u "extra depth" against a host line where nothing had moved)
	if (scen ~ /^h(push|loose|pushed|ride|climb)$/ && mvh[key, "H"] > 100 && mvh[key, "HOST"] < 0.8 * mvh[key, "H"]) { bad[scen]++; next }
	# the GRAB scenarios: on the HELD PROP's travel — neither A nor H moves there, so a host that recorded nothing went
	# unnoticed (2026-11-13: one hstack run at fakelag 90 had the editor stall ~205 s mid-run — its samples jumped to
	# t = 214 s in a 9 s run — and the host's line showed the stool never lifted while all three clients had it at 83u)
	if (scen ~ /^(hstack|hclose)$/ && mvl[key, "H"] > 40 && mvl[key, "HOST"] < 0.8 * mvl[key, "H"]) { bad[scen]++; next }
	if (mx > dv[scen, "HOST"]) dv[scen, "HOST"] = mx
	n = split("A B H", rr, " ")
	for (i = 1; i <= n; i++) {
		r = rr[i]; if (!((key, r) in d)) continue
		ex = d[key, r] - d[key, "HOST"]; if (!((scen, r) in dmax) || ex > dmax[scen, r]) dmax[scen, r] = ex; dsum[scen, r] += ex
		ex = c[key, "HOST"] - c[key, r]; if (!((scen, r) in cmax) || ex > cmax[scen, r]) cmax[scen, r] = ex
		if ((scen == "jump" || scen == "ledge" || scen == "hride" || scen == "hstack") && v[key, "HOST"] != 999 && v[key, r] != 999 && v[key, "HOST"] != "" && v[key, r] != "") { ex = v[key, "HOST"] - v[key, r]; if (!((scen, r) in vmax) || ex > vmax[scen, r]) vmax[scen, r] = ex }
		if (ra[key, r] != "") { rasum[scen, r] += ra[key, r]; ran[scen, r]++ }
		if (ga[key, r] != "") { gasum[scen, r] += ga[key, r]; gan[scen, r]++ }
		cnt[scen, r]++
	}
	if (ra[key, "HOST"] != "") { rasum[scen, "HOST"] += ra[key, "HOST"]; ran[scen, "HOST"]++ }
}
END {
	printf "%-12s | %-44s | %-44s | %-44s | %s\n", "scenario", "A: depth+ max/mean  centre+  vgap+  dv  blips  r-a", "B: (same)", "H: (same)", "host dv  r-a  runs"
	for (k = 1; k <= nscen; k++) {
		sc = names[k]; printf "%-12s", sc
		split("A B H", rr, " ")
		for (i = 1; i <= 3; i++) { r = rr[i]
			vg = ((sc, r) in vmax) ? sprintf("%5.1f", vmax[sc, r]) : "    -"
			rr_ = ran[sc, r] ? sprintf("%5.0f/%4.0f", rasum[sc, r] / ran[sc, r], gasum[sc, r] / (gan[sc, r] ? gan[sc, r] : 1)) : "    -"
			printf " | %5.1f/%5.1f %6.1f %s %5d %2d %s", dmax[sc, r], (cnt[sc, r] ? dsum[sc, r] / cnt[sc, r] : 0), cmax[sc, r], vg, dv[sc, r], bl[sc, r], rr_ }
		printf " | %5d %s  %d (%d invalid)\n", dv[sc, "HOST"], (ran[sc, "HOST"] ? sprintf("%5.0f", rasum[sc, "HOST"] / ran[sc, "HOST"]) : "    -"), run[sc], bad[sc]
	}
	# climbing: per screen, each run's "A|H <climbing %> <faces> [faces in order] maxz <highest>" (the host's line is the truth)
	hdr = 0
	for (k = 1; k <= nscen; k++) { sc = names[k]; if (!hascl[sc]) continue
		if (!hdr) { print ""; print "climbing (per run: who climbs, % of frames climbing, faces climbed [in order], highest z):"; hdr = 1 }
		printf "  %-8s", sc; split("A B H HOST", rr, " ")
		for (i = 1; i <= 4; i++) if ((sc, rr[i]) in cl) printf " | %s: %s", rr[i], cl[sc, rr[i]]
		printf "\n"
	}
	# jump watch (SimLabJumpWatch): drawn frames over 1500 u/s or 60° on any prop or hunter, any time (between scenarios too —
	# counted at the next run), outside the lab's own setup teleports. Each screen's count, then its first few details.
	hdr = 0
	for (k = 1; k <= nscen; k++) { sc = names[k]; if (!hasj[sc]) continue
		split("A B H HOST", rr, " ")
		tot = 0; for (i = 1; i <= 4; i++) tot += js[sc, rr[i]]
		if (!hdr) { print ""; print "jump watch (frames >1500 u/s or >60 deg on any prop/hunter, outside the lab's setup teleports; per screen: jumps / setup teleports):"; hdr = 1 }
		printf "  %-12s", sc; for (i = 1; i <= 4; i++) printf " | %s %d / %d", rr[i], js[sc, rr[i]], jset[sc, rr[i]]
		printf "\n"
		if (tot > 0) for (i = 1; i <= 4; i++) if (jd[sc, rr[i]] != "") printf "%s\n", substr(jd[sc, rr[i]], 2)
	}
}
END {
	# grabs (hstack, hclose): per screen and run, where it ends up drawing the held prop L (and hstack's cushion C), station-local.
	# The host's is the truth; the hold (grip, chase, holder filter) runs on every machine, so they should agree.
	hdr = 0
	for (k = 1; k <= nscen; k++) { sc = names[k]; if (!hasge[sc]) continue
		if (!hdr) { print ""; print "grabs (per run: where the held prop ends up drawn, station-local):"; hdr = 1 }
		split("A B H HOST", rr, " ")
		for (i = 1; i <= 4; i++) if ((sc, rr[i]) in ge) printf "  %-8s %-4s %s\n", sc, rr[i], ge[sc, rr[i]]
	}
}
