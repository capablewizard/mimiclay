// Option 1 hollow for the user's wheelbarrow tray: one Subtract box per inner wall (side walls in layers),
// each box touching its own inner plane and kept inside every other inner plane.
// MODE: "plan" = report only; "apply" = disable brush 8 (user's hollow) and insert the boxes after it.
var MODE = System.IO.File.ReadAllText(System.IO.Path.Combine(exchangeDirectory, "hollow_mode.txt")).Trim();
const float T_WALL = 7f, DEPTH_WALL = 28f, ROUND = 20f, BLEND = 5f;
const int SIDE_LAYERS = 3, FB_LAYERS = 2;

var s = Editor.SceneEditorSession.Active;
var wb = s.Scene.Directory.FindByGuid(new Guid("79c4bccc-8345-4cc7-95a4-af217b499364"));
var sc = wb.Components.Get<Mimiclay.SdfSculpture>();
var B = sc.Brushes;
var sb = new System.Text.StringBuilder();
var T = new Vector3(27f, 0f, 90f); // tray interior reference point

// inner plane = (point, inward normal)
(Vector3 q, Vector3 m) InnerPlane(Vector3 c, Rotation r, Vector3 h)
{
	var axes = new[] { r.Forward, -r.Forward, r.Left, -r.Left, r.Up, -r.Up };
	var hs = new[] { h.x, h.x, h.y, h.y, h.z, h.z };
	float best = -1e9f; int bi = 0;
	for (int i = 0; i < 6; i++) { float d = Vector3.Dot(T - c, axes[i]) - hs[i]; if (d > best) { best = d; bi = i; } }
	var n = axes[bi];
	return (c + n * (hs[bi] + T_WALL), n);
}
Rotation MirY(Rotation r) => new Rotation(-r.x, r.y, -r.z, r.w);

var planes = new System.Collections.Generic.Dictionary<string, (Vector3 q, Vector3 m)>();
planes["floor"] = InnerPlane(B[2].Position, B[2].Rotation, B[2].Size);
planes["front"] = InnerPlane(B[3].Position, B[3].Rotation, B[3].Size);
planes["back"]  = InnerPlane(B[4].Position, B[4].Rotation, B[4].Size);
planes["sideL"] = InnerPlane(B[5].Position, B[5].Rotation, B[5].Size);
var p5 = B[5].Position;
planes["sideR"] = InnerPlane(new Vector3(p5.x, -p5.y, p5.z), MirY(B[5].Rotation), B[5].Size);
// rim height = bottom of the top slice (brush 7)
float rimZ = B[7].Position.z - B[7].Size.z;
// --- measure the REAL outer walls from the field (cut blends of 32 move them well inside the cut-box faces) ---
// work on a copy with any previously inserted hollow boxes (12..19) disabled
var probe = B.Select(b => b.Copy()).ToList();
for (int i = 12; i < probe.Count; i++) if (probe[i].Operation == Mimiclay.SdfOperation.Subtract && probe[i].Shape == Mimiclay.SdfShape.Box && probe[i].Blend == BLEND && probe[i].Rounding >= 10) probe[i].Enabled = false;
var sharp = new System.Collections.Generic.Dictionary<string, (Vector3 q, Vector3 m)>(planes);
foreach (var key in sharp.Keys.ToList())
{
	var m0 = sharp[key].m;
	var tu = Vector3.Cross(m0, Math.Abs(m0.z) < 0.9f ? Vector3.Up : Vector3.Forward).Normal;
	var tv = Vector3.Cross(m0, tu).Normal;
	var hits = new System.Collections.Generic.List<Vector3>();
	foreach (var a in new[] { -0.3f, -0.15f, 0f, 0.15f, 0.3f })
	foreach (var bb in new[] { -0.3f, -0.15f, 0f, 0.15f, 0.3f })
	{
		var dir = (-m0 + tu * a + tv * bb).Normal;
		float tt = 0; bool found = false;
		for (; tt < 250; tt += 0.5f) { var p = T + dir * tt; if (Vector3.Dot(p, Vector3.Up) > rimZ - 4) break; if (Mimiclay.Sdf.Sample(probe, p) > 0) { found = true; break; } }
		if (found) hits.Add(T + dir * tt);
	}
	// least-squares plane: dominant axis of m0 as a linear function of the other two
	int k = Math.Abs(m0.x) >= Math.Abs(m0.y) && Math.Abs(m0.x) >= Math.Abs(m0.z) ? 0 : (Math.Abs(m0.y) >= Math.Abs(m0.z) ? 1 : 2);
	float G(Vector3 p, int i) => i == 0 ? p.x : i == 1 ? p.y : p.z;
	int i1 = (k + 1) % 3, i2 = (k + 2) % 3;
	double s11 = 0, s12 = 0, s22 = 0, s1 = 0, s2 = 0, sy = 0, s1y = 0, s2y = 0; int n = hits.Count;
	foreach (var p in hits) { double x1 = G(p, i1), x2 = G(p, i2), y = G(p, k); s11 += x1 * x1; s12 += x1 * x2; s22 += x2 * x2; s1 += x1; s2 += x2; sy += y; s1y += x1 * y; s2y += x2 * y; }
	// solve [n s1 s2; s1 s11 s12; s2 s12 s22] [c0 c1 c2] = [sy s1y s2y]
	double[,] A = { { n, s1, s2, sy }, { s1, s11, s12, s1y }, { s2, s12, s22, s2y } };
	for (int c = 0; c < 3; c++) { int piv = c; for (int r = c + 1; r < 3; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[piv, c])) piv = r; for (int j = 0; j < 4; j++) { var tmp = A[c, j]; A[c, j] = A[piv, j]; A[piv, j] = tmp; } for (int r = 0; r < 3; r++) { if (r == c) continue; var fct = A[r, c] / A[c, c]; for (int j = 0; j < 4; j++) A[r, j] -= fct * A[c, j]; } }
	double c1 = A[1, 3] / A[1, 1], c2 = A[2, 3] / A[2, 2];
	var nv = new float[3]; nv[k] = 1; nv[i1] = (float)-c1; nv[i2] = (float)-c2;
	var nrm = new Vector3(nv[0], nv[1], nv[2]).Normal;
	if (Vector3.Dot(nrm, m0) < 0) nrm = -nrm; // inward
	float maxS = hits.Max(p => Vector3.Dot(p, nrm));
	var qIn = nrm * (maxS + T_WALL);
	planes[key] = (qIn, nrm);
	sb.AppendLine($"{key}: {hits.Count} hits, fitted inward normal {nrm} (sharp {m0}), surface spread {maxS - hits.Min(p => Vector3.Dot(p, nrm)):F1}");
}
foreach (var kv in planes) sb.AppendLine($"{kv.Key}: q={kv.Value.q} m={kv.Value.m}");
sb.AppendLine($"rimZ={rimZ:F1}");

bool Inside(Vector3 p, string skip, float tol = 0.5f)
{
	foreach (var kv in planes) { if (kv.Key == skip) continue; if (Vector3.Dot(p - kv.Value.q, kv.Value.m) < -tol) return false; }
	return true;
}

// a box on wall `w`: face rect [u0,u1]x[v0,v1] on the inner plane, depth D inward
var boxes = new System.Collections.Generic.List<(string name, Vector3 c, Rotation r, Vector3 half, bool mirY)>();
void BuildOnWall(string w, float vStartFrac, float depth, bool mirY, string label)
{
	var (q, m) = planes[w];
	Vector3 v, u;
	if (w == "floor") { u = (Vector3.Left - m * Vector3.Dot(Vector3.Left, m)).Normal; v = Vector3.Cross(m, u).Normal; }
	else { v = (Vector3.Up - m * Vector3.Dot(Vector3.Up, m)).Normal; u = Vector3.Cross(v, m).Normal; }
	// v extent: from the lowest point the box may reach up to well past the rim
	float vTop = 400f;
	// find vMin: lowest v where the face's centre line stays inside (search)
	float vMin = -400f;
	while (vMin < vTop && !Inside(q + v * vMin, w)) vMin += 1f;
	float vMaxInside = vMin; // highest v still below the rim on the centre line
	while (Vector3.Dot(q + v * (vMaxInside + 1), Vector3.Up) < rimZ && vMaxInside < vTop) vMaxInside += 1f;
	float v0 = (w == "floor") ? vMin : vMin + (vMaxInside - vMin) * vStartFrac;
	float v1 = (w == "floor") ? vMin : vTop; // floor box: u/v are in-plane (horizontal-ish); handled below
	float u0 = -400f, u1 = 400f;
	if (w == "floor")
	{
		// floor: u = across (y), v = along (x). grow both both ways from the centre line.
		float a0 = 0, a1 = 0, b0 = 0, b1 = 0;
		while (Inside(q + v * (vMin + a1 + 1), w)) a1 += 1; // along +v
		while (Inside(q + v * (vMin - a0 - 1), w)) a0 += 1; // along -v
		v0 = vMin - a0; v1 = vMin + a1; u0 = -400; u1 = 400;
	}
	// shrink one edge per pass until every sampled corner/edge point satisfies every other plane (below the rim + 40).
	// Sideways (u) fixes take priority, so a sloping floor doesn't push the box bottom up while u is still huge.
	for (int it = 0; it < 6000; it++)
	{
		int fixU = -1, fixV = -1;
		for (int iu = 0; iu < 2; iu++)
		for (int iv = 0; iv < 2; iv++)
		for (int k = 0; k <= 4; k++)
		{
			float uu = iu == 0 ? u0 : u1, vv = iv == 0 ? v0 : v1, dd = depth * k / 4f;
			var p = q + u * uu + v * vv + m * dd;
			if (Vector3.Dot(p, Vector3.Up) > rimZ + 40) continue;
			foreach (var kv in planes)
			{
				if (kv.Key == w) continue;
				if (Vector3.Dot(p - kv.Value.q, kv.Value.m) >= -0.5f) continue;
				float du = Math.Abs(Vector3.Dot(kv.Value.m, u)), dv = Math.Abs(Vector3.Dot(kv.Value.m, v));
				if (du >= dv) { if (fixU < 0) fixU = iu; }
				else { if (fixV < 0) fixV = (w == "floor" && iv == 1) ? 3 : 2; }
			}
		}
		int fix = fixU >= 0 ? fixU : fixV;
		if (fix < 0) break;
		if (fix == 0) u0 += 1; else if (fix == 1) u1 -= 1; else if (fix == 2) v0 += 1; else v1 -= 1;
		if (u1 - u0 < 4 || v1 - v0 < 4) break;
	}
	var centre = q + u * ((u0 + u1) / 2) + v * ((v0 + v1) / 2) + m * (depth / 2);
	var rot = Rotation.LookAt(u, -m); // Forward = u, Up = -m (out of the cavity), Left = v (sign checked below)
	var left = rot.Left;
	var half = new Vector3((u1 - u0) / 2, (v1 - v0) / 2, depth / 2);
	boxes.Add((label, centre, rot, half, mirY));
	sb.AppendLine($"{label}: u[{u0:F0},{u1:F0}] v[{v0:F0},{v1:F0}] D={depth} centre={centre} half={half} left·v={Vector3.Dot(left, v):F2}");
}

void Best(string w, float frac, bool mirY, string label)
{
	int bestI = -1; float bestVol = -1;
	var start = boxes.Count;
	foreach (var dpt in new[] { 20f, 30f, 45f, 60f, 80f })
	{
		BuildOnWall(w, frac, dpt, mirY, label + "@" + dpt);
		var b = boxes[^1];
		float vol = b.half.x * Math.Min(b.half.y, 60f) * b.half.z; // cap the above-rim height so it doesn't dominate
		if (vol > bestVol) { bestVol = vol; bestI = boxes.Count - 1; }
	}
	var keep = boxes[bestI];
	boxes.RemoveRange(start, boxes.Count - start);
	boxes.Add(keep);
	sb.AppendLine($"  => kept {keep.name}");
}
BuildOnWall("floor", 0, 300f, false, "floor");
for (int k = 0; k < FB_LAYERS; k++) Best("front", k / (float)FB_LAYERS, false, $"front{k}");
for (int k = 0; k < FB_LAYERS; k++) Best("back", k / (float)FB_LAYERS, false, $"back{k}");
for (int k = 0; k < SIDE_LAYERS; k++) Best("sideL", k / (float)SIDE_LAYERS, true, $"side{k}");

// coverage: sample the cavity below the rim
bool InBox(Vector3 p, (string name, Vector3 c, Rotation r, Vector3 half, bool mirY) b)
{
	var l = p - b.c; var lx = Vector3.Dot(l, b.r.Forward); var ly = Vector3.Dot(l, b.r.Left); var lz = Vector3.Dot(l, b.r.Up);
	return Math.Abs(lx) <= b.half.x && Math.Abs(ly) <= b.half.y && Math.Abs(lz) <= b.half.z;
}
int tot = 0, miss = 0; float worst = 0; Vector3 worstP = default;
for (float x = -120; x <= 180; x += 4)
for (float y = -120; y <= 120; y += 4)
for (float z = 0; z <= rimZ - 2; z += 4)
{
	var p = new Vector3(x, y, z);
	bool inCav = true; float depthIn = 1e9f;
	foreach (var kv in planes) { float d = Vector3.Dot(p - kv.Value.q, kv.Value.m); if (d < 0) { inCav = false; break; } depthIn = Math.Min(depthIn, d); }
	if (!inCav) continue;
	tot++;
	bool cov = false;
	foreach (var b in boxes)
	{
		if (InBox(p, b)) { cov = true; break; }
		if (b.mirY && InBox(new Vector3(p.x, -p.y, p.z), b)) { cov = true; break; }
	}
	if (!cov) { miss++; if (depthIn > worst) { worst = depthIn; worstP = p; } }
}
sb.AppendLine($"coverage: {tot - miss}/{tot} = {(tot > 0 ? 100f * (tot - miss) / tot : 0):F1}%  worst uncovered depth={worst:F1} at {worstP}");

if (MODE == "apply")
{
	var t = typeof(Mimiclay.SdfSculpture);
	bool had = sc.BrushObjects;
	if (had) t.GetMethod("CollapseToList").Invoke(sc, null);
	var list = sc.Brushes;
	list.RemoveAll(b => b.Operation == Mimiclay.SdfOperation.Subtract && b.Shape == Mimiclay.SdfShape.Box && b.Rounding >= 10 && b.Blend == BLEND && b.Enabled);
	list[8].Enabled = false;
	var col = list[8].Color;
	int at = 12; // after the old (disabled) hollow brushes 9-11, before the wheel
	foreach (var b in boxes)
	{
		var nb = list[8].Copy();
		nb.Id = Guid.NewGuid();
		nb.Enabled = true; nb.Shape = Mimiclay.SdfShape.Box; nb.Operation = Mimiclay.SdfOperation.Subtract;
		nb.Position = b.c; nb.Rotation = b.r; nb.Size = b.half; nb.Rounding = ROUND; nb.Blend = BLEND;
		nb.MirrorX = false; nb.MirrorY = b.mirY; nb.MirrorZ = false; nb.Color = col;
		list.Insert(at++, nb);
	}
	sc.Brushes = list;
	if (had) t.GetMethod("ExplodeToObjects").Invoke(sc, null);
	sc.Rebuild();
	s.HasUnsavedChanges = true;
	sb.AppendLine($"applied {boxes.Count} boxes at index 12..{at - 1}; brush 8 disabled; total brushes {sc.Brushes.Count}");
}
return sb.ToString();
