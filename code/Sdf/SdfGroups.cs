using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>A reusable multi-brush shape the editor treats as ONE brush: a mouth, a hat, a pre-fabricated
/// prop part. Authored as an ordinary SdfSculpture prefab (its brushes in prefab-local space, the pivot at the
/// prefab origin); placed as a single <see cref="SdfShape.Group"/> brush whose Position/Rotation/Size move,
/// turn and scale the whole set. One or more VARIANTS — alternative prefabs swapped per machine at render
/// time (variant 1 = "talking" for a mouth); a kind with one prefab shows it for every variant.</summary>
public sealed class SdfGroupKind
{
	/// <summary>Stable id carried by every placed brush (<see cref="SdfBrush.GroupKind"/>) — saves, prefabs
	/// and the wire all name the kind by this string, so never rename one that has shipped.</summary>
	public string Id { get; init; }

	/// <summary>Shape-dock label.</summary>
	public string Label { get; init; }

	/// <summary>Prefab paths per variant (assets-root relative). Index 0 is the idle look and the SCALE
	/// REFERENCE — a placed brush's Size is this prefab's bare half-extents times the group's scale, so
	/// swapping to another variant never rescales the brush.</summary>
	public string[] Prefabs { get; init; }

	/// <summary>Only offered by HEAD sessions (the player's face editor), never for props.</summary>
	public bool HeadOnly { get; init; }

	/// <summary>Material Icons glyph for the layer row / dock fallback.</summary>
	public string Glyph { get; init; } = "category";
}

/// <summary>Which variant of a group's prefab set a machine shows right now. Runtime-only — chosen per
/// machine from gameplay state (voice), never saved or networked.</summary>
public static class SdfGroupVariant
{
	public const int Idle = 0;
	public const int Talking = 1;
}

/// <summary>Registry of <see cref="SdfGroupKind"/>s plus the expansion that turns a placed group brush into
/// the concrete member brushes every geometry consumer evaluates (<see cref="SdfBrush.Members"/>).</summary>
public static class SdfGroups
{
	public const string Mouth = "mouth";

	static readonly List<SdfGroupKind> _kinds = new()
	{
		new SdfGroupKind
		{
			Id = Mouth,
			Label = "Mouth",
			Prefabs = new[] { "prefabs/playerheads/mouth_smile.prefab", "prefabs/playerheads/mouth_open.prefab" },
			HeadOnly = true,
			Glyph = "sentiment_satisfied",
		},
	};

	public static IReadOnlyList<SdfGroupKind> Kinds => _kinds;

	public static SdfGroupKind Find( string id )
	{
		if ( string.IsNullOrEmpty( id ) )
			return null;
		foreach ( var k in _kinds )
			if ( string.Equals( k.Id, id, StringComparison.OrdinalIgnoreCase ) )
				return k;
		return null;
	}

	/// <summary>Kinds a session may offer: head sessions get everything, others only the non-head kinds.</summary>
	public static IEnumerable<SdfGroupKind> KindsFor( bool headSession )
		=> headSession ? _kinds : _kinds.Where( k => !k.HeadOnly );

	// ── templates ────────────────────────────────────────────────────────────────────────────────────

	sealed class Template
	{
		public List<SdfBrush> Brushes = new();
		public Vector3 Centre;   // bare (no blend) bounds centre of the ADD members, prefab-local
		public Vector3 Extents;  // bare half-extents, floored so a flat prefab still scales sanely
	}

	static readonly Dictionary<(string Kind, int Variant), Template> _templates = new();

	/// <summary>Bumped when the template cache is dropped (editor hot-reload) so every cached expansion
	/// re-derives. Part of <see cref="SdfBrush.Members"/>'s cache key.</summary>
	public static int Version { get; private set; } = 1;

	/// <summary>Forget every loaded prefab (next access reloads). For the editor / a console command after
	/// re-authoring a group prefab; harmless at runtime.</summary>
	public static void Reload()
	{
		_templates.Clear();
		Version++;
	}

	static readonly Template Empty = new() { Extents = new Vector3( 1f ) }; // unknown kind: no members, unit scale (never divide by 0)

	static Template Load( string kindId, int variant )
	{
		var kind = Find( kindId );
		if ( kind is null || kind.Prefabs is not { Length: > 0 } )
			return Empty;

		// Missing variants fall back to idle (a hat has no "talking" look); the cache key is the REQUESTED
		// variant so the fallback is a cheap dictionary hit next time too.
		int v = Math.Clamp( variant, 0, kind.Prefabs.Length - 1 );
		var key = (kind.Id, variant);
		if ( _templates.TryGetValue( key, out var t ) )
			return t;

		t = new Template();
		var brushes = LoadPrefabBrushes( kind.Prefabs[v] );
		if ( brushes is not null )
			t.Brushes = brushes;

		// Scale reference comes from the IDLE prefab whatever variant this is (see SdfGroupKind.Prefabs).
		var reference = v == 0 ? t : Load( kindId, 0 );
		if ( v == 0 )
			MeasureBare( t );
		else
		{
			t.Centre = reference.Centre;
			t.Extents = reference.Extents;
		}

		_templates[key] = t;
		return t;
	}

	static List<SdfBrush> LoadPrefabBrushes( string path )
	{
		try
		{
			var file = ResourceLibrary.Get<PrefabFile>( path );
			var sculpt = file is null ? null : SceneUtility.GetPrefabScene( file )?.GetAllComponents<SdfSculpture>().FirstOrDefault();
			if ( sculpt?.Brushes is not { Count: > 0 } src )
			{
				Log.Warning( $"SdfGroups: group prefab \"{path}\" has no SdfSculpture brushes." );
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
				c.MirrorX = c.MirrorY = c.MirrorZ = false; // symmetry comes from the PLACED brush, not the prefab
				list.Add( c );
			}
			return list;
		}
		catch ( Exception e )
		{
			Log.Warning( $"SdfGroups: failed to load group prefab \"{path}\": {e.Message}" );
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
	public static Vector3 DefaultSize( string kindId ) => Load( kindId, 0 ).Extents;

	/// <summary>The idle prefab's bare bounds centre in prefab space — scaled by the brush, this is the
	/// group's <see cref="SdfBrush.LocalCentre"/> (the pivot is the prefab origin, not its middle).</summary>
	public static Vector3 ReferenceCentre( string kindId ) => Load( kindId, 0 ).Centre;

	/// <summary>Per-axis scale a group brush applies to its prefab: Size over the idle prefab's half-extents.</summary>
	public static Vector3 Scale( SdfBrush g )
	{
		var e = Load( g.GroupKind, 0 ).Extents;
		return new Vector3( g.Size.x / e.x, g.Size.y / e.y, g.Size.z / e.z );
	}

	/// <summary>The material a new group stamp wears: the first colour-linked member's (the lips), so a mouth
	/// doesn't arrive wearing whatever swatch the user last painted the face with.</summary>
	public static (Color Color, float Metallic, float Roughness, float Blend)? DefaultMaterial( string kindId )
	{
		foreach ( var m in Load( kindId, 0 ).Brushes )
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
			var e = m.Copy();
			e.Id = MemberId( g.Id, i );
			e.GroupKind = null;
			e.GroupVariant = 0;
			e.LinkColor = false;
			e.LinkBlend = false;

			e.Position = pos + rot * (m.Position * scale);
			e.Rotation = rot * m.Rotation;
			e.Size = m.Size * LocalScale( m.Rotation, scale );

			if ( m.Points is { } pts )
			{
				var np = new List<Vector4>( pts.Count );
				foreach ( var p in pts )
				{
					var q = pos + rot * (new Vector3( p.x, p.y, p.z ) * scale);
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
}
