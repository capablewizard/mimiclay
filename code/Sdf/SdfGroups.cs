using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>A reusable multi-brush shape the editor treats as ONE brush: a mouth, a hat, a pre-fabricated
/// prop part. An ASSET (<c>.bgroup</c> — make several from the editor's Asset Browser, New → Brush Group),
/// authored as ordinary SdfSculpture prefabs (brushes in prefab-local space, the pivot at the prefab origin)
/// and placed as a single <see cref="SdfShape.Group"/> brush whose Position/Rotation/Size move, turn and
/// scale the whole set. Members flagged <see cref="SdfBrush.LinkColor"/>/<see cref="SdfBrush.LinkBlend"/> in
/// the prefab wear the placed brush's material/blend; the rest keep the prefab's.
/// <para>VARIANTS — alternative prefabs swapped per machine at render time, never saved or networked:
/// <see cref="Idle"/> is the resting look and the SCALE REFERENCE (a placed brush's Size is its bare
/// half-extents times the group's scale, so swapping variants never rescales); <see cref="Talking"/> shows
/// while the player's mic is hot; <see cref="Visemes"/> are the per-mouth-shape prefabs a lip-sync driver
/// picks from (missing ones fall back to Talking, then Idle).</para></summary>
[AssetType( Name = "Brush Group", Extension = "bgroup", Category = "Mimiclay" )]
[Icon( "sentiment_satisfied" )]
public sealed class SdfGroupKind : GameResource
{
	/// <summary>Stable id carried by every placed brush (<see cref="SdfBrush.GroupKind"/>): the asset's path.
	/// Saves, prefabs and the wire all name the kind by it, so moving/renaming a shipped asset orphans brushes.</summary>
	public string Id => ResourcePath;

	/// <summary>Shape-dock tile label.</summary>
	[Property] public string Label { get; set; } = "Group";

	/// <summary>Dock order among groups (lowest first; the first group is also the Slot8 hotkey).</summary>
	[Property] public int Order { get; set; }

	/// <summary>Only offered by HEAD sessions (the player's face editor), never for props.</summary>
	[Property] public bool HeadOnly { get; set; } = true;

	/// <summary>Material Icons glyph for the layer row / dock fallback.</summary>
	[Property] public string Glyph { get; set; } = "sentiment_satisfied";

	/// <summary>Resting look + scale reference. Required — a group with no idle prefab places nothing.</summary>
	[Property, Group( "Shapes" )] public PrefabFile Idle { get; set; }

	/// <summary>Shown while the player speaks (the simple open/closed swap). Falls back to Idle.</summary>
	[Property, Group( "Shapes" )] public PrefabFile Talking { get; set; }

	/// <summary>Lip-sync shapes, one per OVR viseme the driver can land on. Unlisted visemes use Talking.</summary>
	[Property, Group( "Shapes" )] public List<VisemeShape> Visemes { get; set; } = new();

	public struct VisemeShape
	{
		public SdfViseme Viseme { get; set; }
		public PrefabFile Prefab { get; set; }
	}

	/// <summary>The prefab a variant index resolves to, with the fallbacks above. Null = nothing to show.</summary>
	public PrefabFile PrefabFor( int variant )
	{
		if ( variant == SdfGroupVariant.Idle )
			return Idle;
		if ( variant == SdfGroupVariant.Talking )
			return Talking ?? Idle;

		var viseme = (SdfViseme)(variant - SdfGroupVariant.VisemeBase);
		if ( Visemes is { } list )
			foreach ( var v in list )
				if ( v.Viseme == viseme && v.Prefab is not null )
					return v.Prefab;
		return Talking ?? Idle;
	}
}

/// <summary>The engine's 15 lip-sync mouth shapes, in OVRLipSync order (matches <c>Sandbox.Visemes</c> and
/// the index of each weight in <c>Voice.Visemes</c>).</summary>
public enum SdfViseme
{
	Silence, PP, FF, TH, DD, KK, CH, SS, NN, RR, AA, E, I, O, U,
}

/// <summary>Which look of a group's prefab set a machine shows right now. Runtime-only — chosen per machine
/// from gameplay state (voice), never saved or networked.</summary>
public static class SdfGroupVariant
{
	public const int Idle = 0;
	public const int Talking = 1;
	/// <summary>Viseme variants: <c>VisemeBase + (int)SdfViseme</c>.</summary>
	public const int VisemeBase = 2;
	public static int ForViseme( SdfViseme v ) => VisemeBase + (int)v;
}

/// <summary>Lookup of <see cref="SdfGroupKind"/> assets plus the expansion that turns a placed group brush into
/// the concrete member brushes every geometry consumer evaluates (<see cref="SdfBrush.Members"/>).</summary>
public static class SdfGroups
{
	/// <summary>Every Brush Group asset in the project, dock order.</summary>
	public static IEnumerable<SdfGroupKind> Kinds =>
		ResourceLibrary.GetAll<SdfGroupKind>().Where( k => k.Idle is not null ).OrderBy( k => k.Order ).ThenBy( k => k.ResourcePath );

	public static SdfGroupKind Find( string id )
	{
		if ( string.IsNullOrEmpty( id ) )
			return null;
		return ResourceLibrary.TryGet<SdfGroupKind>( id, out var k ) ? k : null;
	}

	/// <summary>Kinds a session may offer: head sessions get everything, others only the non-head kinds.</summary>
	public static IEnumerable<SdfGroupKind> KindsFor( bool headSession )
		=> headSession ? Kinds : Kinds.Where( k => !k.HeadOnly );

	// ── templates ────────────────────────────────────────────────────────────────────────────────────

	sealed class Template
	{
		public List<SdfBrush> Brushes = new();
		public Vector3 Centre;   // bare (no blend) bounds centre of the ADD members, prefab-local
		public Vector3 Extents = new( 1f ); // bare half-extents, floored so a flat prefab still scales sanely
	}

	static readonly Dictionary<(string Kind, int Variant), Template> _templates = new();

	/// <summary>Bumped when the template cache is dropped (editor hot-reload) so every cached expansion
	/// re-derives. Part of <see cref="SdfBrush.Members"/>'s cache key.</summary>
	public static int Version { get; private set; } = 1;

	/// <summary>Forget every loaded prefab (next access reloads). For the editor / a console command after
	/// re-authoring a group prefab or asset; harmless at runtime.</summary>
	public static void Reload()
	{
		_templates.Clear();
		Version++;
	}

	static readonly Template Empty = new(); // unknown kind: no members, unit scale (never divide by 0)

	static Template Load( string kindId, int variant )
	{
		var kind = Find( kindId );
		if ( kind is null || kind.Idle is null )
			return Empty;

		var key = (kind.Id, variant);
		if ( _templates.TryGetValue( key, out var t ) )
			return t;

		t = new Template();
		var prefab = kind.PrefabFor( variant );
		var brushes = prefab is null ? null : LoadPrefabBrushes( prefab );
		if ( brushes is not null )
			t.Brushes = brushes;

		// Scale reference comes from the IDLE prefab whatever variant this is (see SdfGroupKind).
		if ( variant == SdfGroupVariant.Idle )
			MeasureBare( t );
		else
		{
			var reference = Load( kindId, SdfGroupVariant.Idle );
			t.Centre = reference.Centre;
			t.Extents = reference.Extents;
		}

		_templates[key] = t;
		return t;
	}

	static List<SdfBrush> LoadPrefabBrushes( PrefabFile file )
	{
		try
		{
			var sculpt = SceneUtility.GetPrefabScene( file )?.GetAllComponents<SdfSculpture>().FirstOrDefault();
			if ( sculpt?.EffectiveBrushes() is not { Count: > 0 } src )
			{
				Log.Warning( $"SdfGroups: group prefab \"{file.ResourcePath}\" has no SdfSculpture brushes." );
				return null;
			}

			var list = new List<SdfBrush>( src.Count );
			foreach ( var b in src )
			{
				if ( b.Damage || b.Shape == SdfShape.Group ) // no craters, no nesting
					continue;
				var c = b.Copy();
				c.Damage = false;
				c.Shrinks = false;
				// Member symmetry is KEPT: Expand bakes it into reflected copies in prefab space (the placed brush's own
				// symmetry is applied on top). Clearing it here is what used to drop a mouth's mirrored teeth.
				list.Add( c );
			}
			return list;
		}
		catch ( Exception e )
		{
			Log.Warning( $"SdfGroups: failed to load group prefab \"{file?.ResourcePath}\": {e.Message}" );
			return null;
		}
	}

	static void MeasureBare( Template t )
	{
		bool any = false;
		Vector3 mn = default, mx = default;
		foreach ( var b in t.Brushes )
		{
			if ( !b.Enabled || b.Operation != SdfOperation.Add )
				continue;
			b.LocalBounds( out var lo, out var hi, includeBlend: false );
			if ( !any ) { mn = lo; mx = hi; any = true; }
			else { mn = Vector3.Min( mn, lo ); mx = Vector3.Max( mx, hi ); }

			// LocalBounds ignores symmetry; a member mirrored inside the prefab (see Expand) spans its reflection too.
			if ( b.EffectiveMirrorX ) { mn.x = MathF.Min( mn.x, -hi.x ); mx.x = MathF.Max( mx.x, -lo.x ); }
			if ( b.EffectiveMirrorY ) { mn.y = MathF.Min( mn.y, -hi.y ); mx.y = MathF.Max( mx.y, -lo.y ); }
			if ( b.EffectiveMirrorZ ) { mn.z = MathF.Min( mn.z, -hi.z ); mx.z = MathF.Max( mx.z, -lo.z ); }
		}

		if ( !any )
		{
			t.Centre = Vector3.Zero;
			t.Extents = new Vector3( 1f );
			return;
		}

		t.Centre = (mn + mx) * 0.5f;
		var e = (mx - mn) * 0.5f;
		float floor = MathF.Max( 0.5f, MathF.Max( e.x, MathF.Max( e.y, e.z ) ) * 0.05f );
		t.Extents = new Vector3( MathF.Max( e.x, floor ), MathF.Max( e.y, floor ), MathF.Max( e.z, floor ) );
	}

	// ── per-brush queries ─────────────────────────────────────────────────────────────────────────────

	/// <summary>The Size a freshly placed group brush gets: the idle prefab at scale 1.</summary>
	public static Vector3 DefaultSize( string kindId ) => Load( kindId, SdfGroupVariant.Idle ).Extents;

	/// <summary>The idle prefab's bare bounds centre in prefab space — scaled by the brush, this is the
	/// group's <see cref="SdfBrush.LocalCentre"/> (the pivot is the prefab origin, not its middle).</summary>
	public static Vector3 ReferenceCentre( string kindId ) => Load( kindId, SdfGroupVariant.Idle ).Centre;

	/// <summary>Per-axis scale a group brush applies to its prefab: Size over the idle prefab's half-extents.</summary>
	public static Vector3 Scale( SdfBrush g )
	{
		var e = Load( g.GroupKind, SdfGroupVariant.Idle ).Extents;
		return new Vector3( g.Size.x / e.x, g.Size.y / e.y, g.Size.z / e.z );
	}

	/// <summary>The material a new group stamp wears: the first colour-linked member's (the lips), so a mouth
	/// doesn't arrive wearing whatever swatch the user last painted the face with.</summary>
	public static (Color Color, float Metallic, float Roughness, float Blend)? DefaultMaterial( string kindId )
	{
		foreach ( var m in Load( kindId, SdfGroupVariant.Idle ).Brushes )
			if ( m.LinkColor || m.LinkBlend )
				return (m.Color, m.Metallic, m.Roughness, m.Blend);
		return null;
	}

	/// <summary>Set every group brush's runtime variant. Returns true if any changed (the caller usually
	/// doesn't care — the renderer re-hashes per frame — but a mesh-owning caller might).</summary>
	public static bool SetVariant( List<SdfBrush> brushes, int variant )
	{
		if ( brushes is null )
			return false;
		bool changed = false;
		foreach ( var b in brushes )
		{
			if ( b.Shape != SdfShape.Group || b.GroupVariant == variant )
				continue;
			b.GroupVariant = variant;
			changed = true;
		}
		return changed;
	}

	public static bool Any( List<SdfBrush> brushes )
	{
		if ( brushes is null )
			return false;
		foreach ( var b in brushes )
			if ( b.Shape == SdfShape.Group )
				return true;
		return false;
	}

	/// <summary>The list with every group replaced by its members, in place of the group. Returns the SAME
	/// list when there are no groups (no allocation), so index-based consumers that flatten at their entry
	/// stay consistent with each other as long as they all flatten the same list.</summary>
	public static List<SdfBrush> Flatten( List<SdfBrush> brushes )
	{
		if ( !Any( brushes ) )
			return brushes;

		var dst = new List<SdfBrush>( brushes.Count + 8 );
		foreach ( var b in brushes )
		{
			if ( b.Shape != SdfShape.Group )
			{
				dst.Add( b );
				continue;
			}
			if ( !b.Enabled )
				continue;
			dst.AddRange( b.Members() );
		}
		return dst;
	}

	// ── expansion ─────────────────────────────────────────────────────────────────────────────────────

	/// <summary>Build the concrete member brushes of a placed group brush, in sculpture space. Called by
	/// <see cref="SdfBrush.Members"/> when its cache key changes — never directly.</summary>
	internal static List<SdfBrush> Expand( SdfBrush g )
	{
		var t = Load( g.GroupKind, g.GroupVariant );
		var result = new List<SdfBrush>( t.Brushes.Count );
		if ( t.Brushes.Count == 0 )
			return result;

		var scale = Scale( g );
		float mean = MathF.Cbrt( MathF.Abs( scale.x * scale.y * scale.z ) );
		if ( !float.IsFinite( mean ) || mean <= 0f )
			mean = 1f;
		var rot = g.Rotation;
		var pos = g.Position;
		bool mx = g.EffectiveMirrorX, my = g.EffectiveMirrorY, mz = g.EffectiveMirrorZ;

		for ( int i = 0; i < t.Brushes.Count; i++ )
		{
			var m = t.Brushes[i];

			// A member's OWN symmetry (a tooth mirrored inside the mouth prefab) reflects about the PREFAB origin.
			// That can't ride along as a flag — flags mirror about the host sculpture's origin — so it is baked
			// here into explicit reflected copies in prefab space, before the group transform. The placed
			// brush's symmetry is then stamped on every copy as before, so a mirrored mouth still mirrors whole.
			int cx = m.EffectiveMirrorX ? 1 : 0, cy = m.EffectiveMirrorY ? 1 : 0, cz = m.EffectiveMirrorZ ? 1 : 0;
			for ( int sx = 0; sx <= cx; sx++ )
			for ( int sy = 0; sy <= cy; sy++ )
			for ( int sz = 0; sz <= cz; sz++ )
			{
				var sign = new Vector3( sx == 1 ? -1f : 1f, sy == 1 ? -1f : 1f, sz == 1 ? -1f : 1f );
				bool reflected = sx + sy + sz > 0;
				var localPos = m.Position * sign;
				var localRot = reflected ? SdfBrushGizmos.MirrorRotation( m.Rotation, sign ) : m.Rotation;

				var e = m.Copy();
				e.Id = MemberId( g.Id, i * 8 + sx + sy * 2 + sz * 4 );
				e.GroupKind = null;
				e.GroupVariant = 0;
				e.LinkColor = false;
				e.LinkBlend = false;

				e.Position = pos + rot * (localPos * scale);
				e.Rotation = rot * localRot;
				e.Size = m.Size * LocalScale( localRot, scale );

				if ( m.Points is { } pts )
				{
					var np = new List<Vector4>( pts.Count );
					foreach ( var p in pts )
					{
						var q = pos + rot * (new Vector3( p.x, p.y, p.z ) * sign * scale);
						np.Add( new Vector4( q.x, q.y, q.z, MathF.Max( p.w * mean, 0.05f ) ) );
					}
					e.Points = np;
				}

				e.Blend = m.LinkBlend ? g.Blend : m.Blend * mean;
				e.Gap = m.Gap * mean;
				e.Rounding = Math.Clamp( m.Rounding * mean, SdfBrush.MinRounding, e.MaxRounding() );

				if ( m.LinkColor )
				{
					e.Color = g.Color;
					e.Metallic = g.Metallic;
					e.Roughness = g.Roughness;
				}

				// Symmetry is the PLACED brush's — judged at the group's own position (its deadzone), then
				// stamped onto every member so a mouth mirrors as a whole.
				e.MirrorX = mx;
				e.MirrorY = my;
				e.MirrorZ = mz;

				result.Add( e );
			}
		}
		return result;
	}


	// How much a member's local axes stretch under the group's (prefab-space, per-axis) scale: exact for
	// an unrotated member, the per-axis length of the scaled axis for a rotated one.
	static Vector3 LocalScale( Rotation memberRot, Vector3 scale )
	{
		float Along( Vector3 axis ) => (memberRot * axis * scale).Length;
		return new Vector3( Along( Vector3.Forward ), Along( Vector3.Left ), Along( Vector3.Up ) );
	}

	// Deterministic member identity: the same group expands to the same ids on every machine and every
	// frame, so anything that keys on Id across expansions stays stable.
	static Guid MemberId( Guid group, int index )
	{
		Span<byte> b = stackalloc byte[16];
		group.TryWriteBytes( b );
		b[15] ^= (byte)(index + 1);
		b[14] ^= (byte)((index + 1) >> 8);
		return new Guid( b );
	}

	/// <summary>Cache key for a group brush's expansion: everything Expand reads off the brush, plus the
	/// template generation. Deliberately NOT HashInto (which itself mixes the members).</summary>
	internal static int Key( SdfBrush g )
	{
		var hc = new HashCode();
		hc.Add( Version );
		hc.Add( g.GroupKind );
		hc.Add( g.GroupVariant );
		hc.Add( g.Id );
		hc.Add( g.Position );
		hc.Add( g.Rotation );
		hc.Add( g.Size );
		hc.Add( g.Blend );
		hc.Add( g.Color );
		hc.Add( g.Metallic );
		hc.Add( g.Roughness );
		hc.Add( g.EffectiveMirrorX );
		hc.Add( g.EffectiveMirrorY );
		hc.Add( g.EffectiveMirrorZ );
		return hc.ToHashCode();
	}

	[ConCmd( "mimi_groups_reload" )]
	static void ReloadCmd()
	{
		Reload();
		Log.Info( "SdfGroups: templates dropped — next frame reloads every group prefab." );
	}

	/// <summary>Log what a group kind expands to at a variant (default Idle): the template's brushes and the members
	/// a unit-scale placed brush would produce, mirror copies included. <c>mimi_groups_dump &lt;kind path&gt; [variant]</c>.</summary>
	[ConCmd( "mimi_groups_dump" )]
	static void DumpCmd( string kindId, int variant = 0 )
	{
		var t = Load( kindId, variant );
		Log.Info( $"SdfGroups: kind '{kindId}' variant {variant}: {t.Brushes.Count} template brush(es), centre {t.Centre}, extents {t.Extents}" );
		foreach ( var b in t.Brushes )
			Log.Info( $"  template {b.Shape} {b.Operation} enabled={b.Enabled} pos={b.Position} size={b.Size} mirror={(b.MirrorX ? "X" : "")}{(b.MirrorY ? "Y" : "")}{(b.MirrorZ ? "Z" : "")} effective={(b.EffectiveMirrorX ? "X" : "")}{(b.EffectiveMirrorY ? "Y" : "")}{(b.EffectiveMirrorZ ? "Z" : "")}" );

		var g = new SdfBrush { Shape = SdfShape.Group, GroupKind = kindId, GroupVariant = variant, Size = t.Extents };
		var members = Expand( g );
		Log.Info( $"  expands to {members.Count} member(s):" );
		foreach ( var m in members )
			Log.Info( $"  member {m.Shape} {m.Operation} enabled={m.Enabled} pos={m.Position} size={m.Size}" );
	}

	/// <summary>Debug override for the mouth driver: a variant every local pawn's face shows regardless of
	/// voice (−1 = off). Set by <c>mimi_dbg_mouth</c>.</summary>
	public static int DebugVariant { get; private set; } = -1;

	[ConCmd( "mimi_dbg_mouth" )]
	static void DebugMouthCmd( string shape = "" )
	{
		if ( string.IsNullOrWhiteSpace( shape ) || shape.Equals( "off", StringComparison.OrdinalIgnoreCase ) )
		{
			DebugVariant = -1;
			Log.Info( "mimi_dbg_mouth: off (voice drives the mouth). Usage: mimi_dbg_mouth idle|talking|<viseme: PP FF TH DD KK CH SS NN RR AA E I O U>" );
			return;
		}
		if ( shape.Equals( "idle", StringComparison.OrdinalIgnoreCase ) ) DebugVariant = SdfGroupVariant.Idle;
		else if ( shape.Equals( "talking", StringComparison.OrdinalIgnoreCase ) ) DebugVariant = SdfGroupVariant.Talking;
		else if ( Enum.TryParse<SdfViseme>( shape, true, out var v ) ) DebugVariant = SdfGroupVariant.ForViseme( v );
		else { Log.Warning( $"mimi_dbg_mouth: unknown shape \"{shape}\"." ); return; }
		Log.Info( $"mimi_dbg_mouth: forcing variant {DebugVariant} ({shape})." );
	}
}
