using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

// ── Touching props wake together ─────────────────────────────────────────────────────────────────────────────
// A still prop is often built from several clay objects that rest on or against each other — the wheelbarrow and its
// wheel, a cart and its wheels. Shot or grabbed, only the one that was hit used to become physics; the parts touching it
// stayed frozen (a scene static, a FrozenProps pawn, an anchored sim decoy) and hung in the air or pinned the moving part
// in place. So whatever wakes one prop (PropClaims' shot + grab in creative, RoundManager's shot + inspect grab in prop
// hunt) asks here for the CLUSTER around it and wakes the rest as well.
//
// "Touching" is tested on the clay itself, not boxes: a point where BOTH signed distance fields are within TouchMargin of
// their surfaces. Found by subdividing the overlap of the two (grown) bounds and pruning every cell that can't hold such a
// point — max(dA, dB) is 1-Lipschitz, so a cell whose centre reads more than margin + half its diagonal is empty. One
// sculpture's distance is read first and the other only where the first passes, so most cells cost one sample.
//
// The flood is capped (WakeDepth hops from the prop that was hit, WakeMax props) so a cup on a clay table can't wake a
// whole kitchen through it, and clay bigger than WakeMaxSize is treated as world (a clay floor or counter never wakes,
// and so never passes the wake on to everything standing on it). Host-side, event-time only (a shot, a grab) — never per frame.
public static class PropContacts
{
	[ConVar( "mimi_wake_touching", Help = "Shooting or grabbing a still prop also wakes the still props touching it (1), or only the one hit (0)." )]
	public static bool WakeTouching { get; set; } = true;

	[ConVar( "mimi_wake_touching_depth", Help = "How many hops a wake spreads from the prop hit: 1 = only what touches it, 2 = what touches those too…" )]
	public static int WakeDepth { get; set; } = 2;

	[ConVar( "mimi_wake_touching_maxsize", Help = "Clay longer than this (units, longest side of its bounds) is treated as part of the world — never woken by a touch (a clay floor, wall or counter)." )]
	public static float WakeMaxSize { get; set; } = 400f; // devprophunt's biggest prop, the wheelbarrow, is 301

	/// <summary>At most this many props are woken alongside the one hit.</summary>
	public const int WakeMax = 12;

	/// <summary>Surfaces this close (units) count as touching — resting contact settles a hair apart, and authored
	/// parts are placed by eye.</summary>
	public const float TouchMargin = 1.5f;

	// Stop subdividing once a cell's half-diagonal is this small; and a per-pair sample budget.
	const float CellResolution = 0.75f;
	const int PairBudget = 2500;

	/// <summary>The still props connected to <paramref name="source"/> through contact, NOT including it — breadth-first,
	/// nearest hops first, capped by <see cref="WakeDepth"/> and <see cref="WakeMax"/>. <paramref name="eligible"/> picks
	/// which sculptures can be woken (and so carry the flood on). Empty when <see cref="WakeTouching"/> is off.</summary>
	public static List<SdfSculpture> Cluster( Scene scene, SdfSculpture source, Func<SdfSculpture, bool> eligible )
	{
		var found = new List<SdfSculpture>();
		if ( !WakeTouching || WakeDepth <= 0 || scene is null || !source.IsValid() || !TryWorldBounds( source, out var sourceBox ) )
			return found;

		// Every candidate once, with its world bounds (most are rejected right here, by the box test below).
		var candidates = new List<(SdfSculpture Sculpt, BBox Box)>();
		foreach ( var s in scene.GetAllComponents<SdfSculpture>() )
		{
			if ( s == source || !s.IsValid() || !s.Active || !TryWorldBounds( s, out var box ) )
				continue;
			var size = box.Size;
			if ( MathF.Max( size.x, MathF.Max( size.y, size.z ) ) > WakeMaxSize || !eligible( s ) )
				continue; // too big to be a prop (a clay floor or counter: everything on it would wake), or not a still prop
			candidates.Add( (s, box) );
		}
		if ( candidates.Count == 0 )
			return found;

		var taken = new HashSet<SdfSculpture> { source };
		var frontier = new List<(SdfSculpture Sculpt, BBox Box)> { (source, sourceBox) };
		for ( int hop = 0; hop < WakeDepth && frontier.Count > 0 && found.Count < WakeMax; hop++ )
		{
			var next = new List<(SdfSculpture, BBox)>();
			foreach ( var (from, fromBox) in frontier )
			{
				foreach ( var (s, box) in candidates )
				{
					if ( found.Count >= WakeMax )
						break;
					if ( taken.Contains( s ) || !Touching( from, fromBox, s, box ) )
						continue;
					taken.Add( s );
					found.Add( s );
					next.Add( (s, box) );
				}
			}
			frontier = next;
		}
		return found;
	}

	/// <summary>Do these two sculptures' clay surfaces touch (within <see cref="TouchMargin"/>)?</summary>
	public static bool Touching( SdfSculpture a, SdfSculpture b )
		=> TryWorldBounds( a, out var ab ) && TryWorldBounds( b, out var bb ) && Touching( a, ab, b, bb );

	static bool Touching( SdfSculpture a, BBox aBox, SdfSculpture b, BBox bBox )
	{
		// The overlap of the two grown boxes: any touching point lies inside it.
		var mins = Vector3.Max( aBox.Mins, bBox.Mins ) - TouchMargin;
		var maxs = Vector3.Min( aBox.Maxs, bBox.Maxs ) + TouchMargin;
		if ( mins.x > maxs.x || mins.y > maxs.y || mins.z > maxs.z )
			return false;

		var fa = new Field( a );
		var fb = new Field( b );
		int budget = PairBudget;
		return Search( fa, fb, mins, maxs, ref budget );
	}

	// Depth-first over the region; true at the first cell that holds a point near both surfaces.
	static bool Search( in Field a, in Field b, Vector3 mins, Vector3 maxs, ref int budget )
	{
		if ( budget <= 0 )
			return false;

		var centre = (mins + maxs) * 0.5f;
		float half = (maxs - mins).Length * 0.5f;
		float reach = TouchMargin + half;

		budget--;
		float da = a.Distance( centre );
		if ( da > reach )
			return false;
		budget--;
		float db = b.Distance( centre );
		if ( db > reach )
			return false;
		if ( half <= CellResolution )
			return da <= TouchMargin + half && db <= TouchMargin + half;

		// Split along the longest axis only — regions are often long and thin (a wheel against a tray's edge), and halving
		// all three axes of a slab wastes most of its samples.
		var size = maxs - mins;
		int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
		var midMax = axis switch { 0 => maxs.WithX( centre.x ), 1 => maxs.WithY( centre.y ), _ => maxs.WithZ( centre.z ) };
		var midMin = axis switch { 0 => mins.WithX( centre.x ), 1 => mins.WithY( centre.y ), _ => mins.WithZ( centre.z ) };

		// Nearer half first: it's the likelier hit.
		bool lowFirst = a.Distance( (mins + midMax) * 0.5f ) <= a.Distance( (midMin + maxs) * 0.5f );
		budget -= 2;
		return lowFirst
			? Search( a, b, mins, midMax, ref budget ) || Search( a, b, midMin, maxs, ref budget )
			: Search( a, b, midMin, maxs, ref budget ) || Search( a, b, mins, midMax, ref budget );
	}

	// One sculpture's signed distance in world units (brushes are local to the sculpture's own transform).
	readonly struct Field
	{
		readonly List<SdfBrush> _brushes;
		readonly Transform _t;
		readonly float _scale;

		public Field( SdfSculpture s )
		{
			_brushes = s.Brushes;
			_t = s.WorldTransform;
			var sc = s.WorldScale;
			_scale = MathF.Max( MathF.Min( MathF.Abs( sc.x ), MathF.Min( MathF.Abs( sc.y ), MathF.Abs( sc.z ) ) ), 1e-4f );
			SdfModifiers.Apply( _brushes ); // once — SamplePrepared skips it per sample
		}

		public float Distance( Vector3 world ) => Sdf.SamplePrepared( _brushes, _t.PointToLocal( world ) ) * _scale;
	}

	/// <summary>A sculpture's clay as a world AABB (all eight corners of its local bounds — it may be turned).</summary>
	public static bool TryWorldBounds( SdfSculpture s, out BBox box )
	{
		box = default;
		if ( !s.IsValid() || s.Brushes is not { Count: > 0 } || !Sdf.TryGetBounds( s.Brushes, out var local ) )
			return false;

		var t = s.WorldTransform;
		box = BBox.FromPositionAndSize( t.PointToWorld( local.Center ), 0f );
		for ( int i = 0; i < 8; i++ )
		{
			var corner = new Vector3( (i & 1) == 0 ? local.Mins.x : local.Maxs.x, (i & 2) == 0 ? local.Mins.y : local.Maxs.y, (i & 4) == 0 ? local.Mins.z : local.Maxs.z );
			box = box.AddPoint( t.PointToWorld( corner ) );
		}
		return true;
	}
}
