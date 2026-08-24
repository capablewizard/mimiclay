using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>One material slot of an <see cref="FbxPiece"/>: unshared triangle vertices, 3 per triangle.</summary>
public sealed class FbxSubMesh
{
	public string MaterialName = "";
	public List<Vertex> Vertices = new();
	public BBox Bounds = BBox.FromPositionAndSize( 0, 1 );
	bool _hasBounds;

	public void Grow( Vector3 p )
	{
		if ( !_hasBounds ) { Bounds = new BBox( p, p ); _hasBounds = true; }
		else Bounds = Bounds.AddPoint( p );
	}
}

/// <summary>
/// One mesh object out of an FBX file, already converted to s&amp;box space (Z-up, inches) with its
/// transform baked relative to the FBX root. Hierarchies are flattened — parents' transforms are
/// composed into each piece.
/// </summary>
public sealed class FbxPiece
{
	public string Name = "";
	public Vector3 Position;
	public Rotation Rotation = Rotation.Identity;

	/// <summary>Always One — object scale and unit conversion are baked into the vertex data, so
	/// position-tiled (triplanar) materials tile at the same density on every piece. Kept so the
	/// importer can reset stale scale on existing children.</summary>
	public Vector3 Scale = Vector3.One;

	public List<FbxSubMesh> SubMeshes = new();

	/// <summary>Shared control points + triangle indices across all material slots, for the physics mesh.</summary>
	public List<Vector3> CollisionVertices = new();
	public List<int> CollisionIndices = new();

	public bool HasTriangles
	{
		get
		{
			foreach ( var s in SubMeshes )
				if ( s.Vertices.Count > 0 )
					return true;
			return false;
		}
	}
}

/// <summary>
/// Turns a parsed FBX node tree into renderable pieces. Handles Blender's default export: Y-up
/// (converted to Z-up), centimetre units (converted to inches), per-polygon-vertex normals/UVs,
/// per-polygon material slots. Rotation order is assumed XYZ (Blender always exports XYZ).
/// </summary>
public static class FbxSceneReader
{
	public static List<FbxPiece> Read( byte[] fileBytes, float importScale, out List<string> materialNames )
	{
		var root = FbxBinary.Parse( fileBytes );
		materialNames = new List<string>();

		// --- global settings: units + up axis ---
		var unitScaleCm = 1.0; // FBX native unit is cm
		long upAxis = 1;       // 1 = Y-up (FBX default), 2 = Z-up
		var settings = root.Find( "GlobalSettings" )?.Find( "Properties70" );
		if ( settings != null )
		{
			foreach ( var p in settings.FindAll( "P" ) )
			{
				switch ( p.GetString( 0 ) )
				{
					case "UnitScaleFactor": unitScaleCm = p.GetDouble( 4 ); break;
					case "UpAxis": upAxis = p.GetLong( 4 ); break;
				}
			}
		}

		var yUp = upAxis != 2;
		var toUnits = (float)(unitScaleCm / 2.54) * importScale; // cm → inches (s&box units)

		// --- object tables ---
		var models = new Dictionary<long, FbxNode>();
		var geometries = new Dictionary<long, FbxNode>();
		var materials = new Dictionary<long, string>();

		var objects = root.Find( "Objects" );
		if ( objects == null )
			return new List<FbxPiece>();

		foreach ( var o in objects.Children )
		{
			var id = o.GetLong( 0 );
			switch ( o.Name )
			{
				case "Model": models[id] = o; break;
				case "Geometry": geometries[id] = o; break;
				case "Material":
					var name = o.GetObjectName( 1 );
					materials[id] = name;
					if ( !materialNames.Contains( name ) )
						materialNames.Add( name );
					break;
			}
		}

		// --- connections (order matters for material slots) ---
		var parentOf = new Dictionary<long, long>();
		var geometryOf = new Dictionary<long, long>();
		var materialsOf = new Dictionary<long, List<string>>();

		var connections = root.Find( "Connections" );
		if ( connections != null )
		{
			foreach ( var c in connections.FindAll( "C" ) )
			{
				if ( c.GetString( 0 ) != "OO" )
					continue;

				var child = c.GetLong( 1 );
				var parent = c.GetLong( 2 );

				if ( models.ContainsKey( child ) && (parent == 0 || models.ContainsKey( parent )) )
				{
					parentOf[child] = parent;
				}
				else if ( geometries.ContainsKey( child ) && models.ContainsKey( parent ) )
				{
					geometryOf[parent] = child;
				}
				else if ( materials.TryGetValue( child, out var matName ) && models.ContainsKey( parent ) )
				{
					if ( !materialsOf.TryGetValue( parent, out var list ) )
						materialsOf[parent] = list = new List<string>();
					list.Add( matName );
				}
			}
		}

		// --- build pieces ---
		var pieces = new List<FbxPiece>();
		var usedNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var worldCache = new Dictionary<long, (Vector3 t, Rotation r, Vector3 s)>();

		foreach ( var (modelId, modelNode) in models )
		{
			if ( !geometryOf.TryGetValue( modelId, out var geomId ) )
				continue;
			if ( !geometries.TryGetValue( geomId, out var geom ) )
				continue;

			var name = modelNode.GetObjectName( 1 );
			if ( string.IsNullOrWhiteSpace( name ) )
				name = "Mesh";
			var unique = name;
			for ( var n = 2; !usedNames.Add( unique ); n++ )
				unique = $"{name} ({n})";

			var (ft, fr, fs) = WorldTrs( modelId, models, parentOf, worldCache );

			// bake ALL scale (object scale + units) into the vertex data so pieces sit at
			// GameObject scale 1 — position-tiled (triplanar) materials tile consistently
			var bakeScale = (yUp ? new Vector3( fs.x, fs.z, fs.y ) : fs) * toUnits;

			var piece = new FbxPiece
			{
				Name = unique,
				Position = ConvertVector( ft, yUp ) * toUnits,
				Rotation = ConvertRotation( fr, yUp ),
				Scale = Vector3.One,
			};

			var slotMaterials = materialsOf.GetValueOrDefault( modelId ) ?? new List<string>();
			BuildGeometry( geom, piece, slotMaterials, yUp, bakeScale );

			if ( piece.HasTriangles )
				pieces.Add( piece );
		}

		pieces.Sort( ( a, b ) => string.Compare( a.Name, b.Name, StringComparison.OrdinalIgnoreCase ) );
		return pieces;
	}

	// ---------------------------------------------------------------- transforms

	/// <summary>FBX Y-up right-handed → s&amp;box Z-up: rotate +90° about X, i.e. (x, y, z) → (x, -z, y).</summary>
	static Vector3 ConvertVector( Vector3 v, bool yUp )
		=> yUp ? new Vector3( v.x, -v.z, v.y ) : v;

	/// <summary>Same basis change applied to a rotation: the axis converts like a vector (det = +1).</summary>
	static Rotation ConvertRotation( Rotation q, bool yUp )
		=> yUp ? new Rotation( q.x, -q.z, q.y, q.w ) : q;

	static (Vector3 t, Rotation r, Vector3 s) WorldTrs(
		long id, Dictionary<long, FbxNode> models, Dictionary<long, long> parentOf,
		Dictionary<long, (Vector3 t, Rotation r, Vector3 s)> cache )
	{
		if ( id == 0 || !models.TryGetValue( id, out var node ) )
			return (Vector3.Zero, Rotation.Identity, Vector3.One);
		if ( cache.TryGetValue( id, out var cached ) )
			return cached;

		var (lt, lr, ls) = LocalTrs( node );
		var (pt, pr, ps) = WorldTrs( parentOf.GetValueOrDefault( id ), models, parentOf, cache );

		// standard TRS composition (ignores shear from non-uniform parent scale under rotation)
		var world = (pt + pr * (ps * lt), pr * lr, ps * ls);
		cache[id] = world;
		return world;
	}

	static (Vector3 t, Rotation r, Vector3 s) LocalTrs( FbxNode model )
	{
		Vector3 t = Vector3.Zero, s = Vector3.One, rotEuler = Vector3.Zero, preEuler = Vector3.Zero;

		var p70 = model.Find( "Properties70" );
		if ( p70 != null )
		{
			foreach ( var p in p70.FindAll( "P" ) )
			{
				switch ( p.GetString( 0 ) )
				{
					case "Lcl Translation": t = ReadVec3( p ); break;
					case "Lcl Rotation": rotEuler = ReadVec3( p ); break;
					case "Lcl Scaling": s = ReadVec3( p ); break;
					case "PreRotation": preEuler = ReadVec3( p ); break;
				}
			}
		}

		return (t, EulerXyz( preEuler ) * EulerXyz( rotEuler ), s);
	}

	static Vector3 ReadVec3( FbxNode p )
		=> new( (float)p.GetDouble( 4 ), (float)p.GetDouble( 5 ), (float)p.GetDouble( 6 ) );

	/// <summary>FBX euler, XYZ order (X applied first), degrees.</summary>
	static Rotation EulerXyz( Vector3 deg )
	{
		if ( deg == Vector3.Zero )
			return Rotation.Identity;

		var qx = Rotation.FromAxis( new Vector3( 1, 0, 0 ), deg.x );
		var qy = Rotation.FromAxis( new Vector3( 0, 1, 0 ), deg.y );
		var qz = Rotation.FromAxis( new Vector3( 0, 0, 1 ), deg.z );
		return qz * qy * qx;
	}

	// ---------------------------------------------------------------- geometry

	class Layer
	{
		public double[] Data;
		public int[] Index;
		public string Mapping = "";
		public bool IndexToDirect;

		public static Layer From( FbxNode element, string dataName, string indexName )
		{
			if ( element == null )
				return null;
			var layer = new Layer
			{
				Data = element.Find( dataName )?.GetDoubleArray( 0 ),
				Index = element.Find( indexName )?.GetIntArray( 0 ),
				Mapping = element.Find( "MappingInformationType" )?.GetString( 0 ) ?? "",
				IndexToDirect = (element.Find( "ReferenceInformationType" )?.GetString( 0 ) ?? "") == "IndexToDirect",
			};
			return layer.Data == null ? null : layer;
		}

		public int Resolve( int cornerIdx, int ctrlPoint, int polyIdx )
		{
			var i = Mapping switch
			{
				"ByPolygonVertex" => cornerIdx,
				"ByVertice" or "ByVertex" => ctrlPoint,
				"ByPolygon" => polyIdx,
				"AllSame" => 0,
				_ => cornerIdx,
			};

			if ( IndexToDirect && Index != null )
				i = i >= 0 && i < Index.Length ? Index[i] : 0;

			return i;
		}

		public Vector3 GetVec3( int cornerIdx, int ctrlPoint, int polyIdx )
		{
			var i = Resolve( cornerIdx, ctrlPoint, polyIdx ) * 3;
			if ( i < 0 || i + 2 >= Data.Length )
				return Vector3.Zero;
			return new Vector3( (float)Data[i], (float)Data[i + 1], (float)Data[i + 2] );
		}

		public Vector2 GetVec2( int cornerIdx, int ctrlPoint, int polyIdx )
		{
			var i = Resolve( cornerIdx, ctrlPoint, polyIdx ) * 2;
			if ( i < 0 || i + 1 >= Data.Length )
				return Vector2.Zero;
			return new Vector2( (float)Data[i], (float)Data[i + 1] );
		}
	}

	static void BuildGeometry( FbxNode geom, FbxPiece piece, List<string> slotMaterials, bool yUp, Vector3 bakeScale )
	{
		var vertsRaw = geom.Find( "Vertices" )?.GetDoubleArray( 0 );
		var pvi = geom.Find( "PolygonVertexIndex" )?.GetIntArray( 0 );
		if ( vertsRaw == null || pvi == null || vertsRaw.Length < 9 )
			return;

		// mirrored objects (negative scale) flip the winding — compensate so faces stay outward
		var flipWinding = bakeScale.x * bakeScale.y * bakeScale.z < 0;

		// normals counter-scale by the inverse (uniform part cancels in the renormalize)
		var invScale = new Vector3(
			MathF.Abs( bakeScale.x ) > 1e-12f ? 1f / bakeScale.x : 0f,
			MathF.Abs( bakeScale.y ) > 1e-12f ? 1f / bakeScale.y : 0f,
			MathF.Abs( bakeScale.z ) > 1e-12f ? 1f / bakeScale.z : 0f );

		// control points, converted to s&box space with all scale baked in
		var ctrl = new Vector3[vertsRaw.Length / 3];
		for ( var i = 0; i < ctrl.Length; i++ )
		{
			var v = new Vector3( (float)vertsRaw[i * 3], (float)vertsRaw[i * 3 + 1], (float)vertsRaw[i * 3 + 2] );
			ctrl[i] = ConvertVector( v, yUp ) * bakeScale;
		}
		piece.CollisionVertices.AddRange( ctrl );

		var normals = Layer.From( geom.Find( "LayerElementNormal" ), "Normals", "NormalsIndex" );
		var uvs = Layer.From( geom.Find( "LayerElementUV" ), "UV", "UVIndex" );

		var matElement = geom.Find( "LayerElementMaterial" );
		var matMapping = matElement?.Find( "MappingInformationType" )?.GetString( 0 ) ?? "AllSame";
		var matIndices = matElement?.Find( "Materials" )?.GetIntArray( 0 );

		var slotCount = Math.Max( 1, slotMaterials.Count );
		for ( var i = 0; i < slotCount; i++ )
			piece.SubMeshes.Add( new FbxSubMesh { MaterialName = i < slotMaterials.Count ? slotMaterials[i] : $"slot{i}" } );

		// walk polygons: pvi values are control-point indices; a negative value (~v) ends the polygon
		var corners = new List<(int ctrl, int corner)>( 8 );
		var polyIdx = 0;

		for ( var i = 0; i < pvi.Length; i++ )
		{
			var raw = pvi[i];
			var last = raw < 0;
			corners.Add( (last ? ~raw : raw, i) );
			if ( !last )
				continue;

			var slot = 0;
			if ( matIndices != null && matMapping == "ByPolygon" && polyIdx < matIndices.Length )
				slot = Math.Clamp( matIndices[polyIdx], 0, slotCount - 1 );
			var sub = piece.SubMeshes[slot];

			for ( var k = 2; k < corners.Count; k++ )
			{
				if ( flipWinding )
					EmitTriangle( sub, piece, ctrl, corners[0], corners[k], corners[k - 1], polyIdx, normals, uvs, yUp, invScale );
				else
					EmitTriangle( sub, piece, ctrl, corners[0], corners[k - 1], corners[k], polyIdx, normals, uvs, yUp, invScale );
			}

			corners.Clear();
			polyIdx++;
		}
	}

	static void EmitTriangle(
		FbxSubMesh sub, FbxPiece piece, Vector3[] ctrl,
		(int ctrl, int corner) a, (int ctrl, int corner) b, (int ctrl, int corner) c,
		int polyIdx, Layer normals, Layer uvs, bool yUp, Vector3 invScale )
	{
		if ( a.ctrl >= ctrl.Length || b.ctrl >= ctrl.Length || c.ctrl >= ctrl.Length )
			return;

		Vector3 p0 = ctrl[a.ctrl], p1 = ctrl[b.ctrl], p2 = ctrl[c.ctrl];

		var faceNormal = Vector3.Cross( p1 - p0, p2 - p0 );
		if ( faceNormal.LengthSquared < 1e-12f )
			return; // degenerate
		faceNormal = faceNormal.Normal;

		Vector3 n0 = faceNormal, n1 = faceNormal, n2 = faceNormal;
		if ( normals != null )
		{
			// file normals counter-scale by the inverse of the baked scale (inverse-transpose)
			n0 = SafeNormal( ConvertVector( normals.GetVec3( a.corner, a.ctrl, polyIdx ), yUp ) * invScale, faceNormal );
			n1 = SafeNormal( ConvertVector( normals.GetVec3( b.corner, b.ctrl, polyIdx ), yUp ) * invScale, faceNormal );
			n2 = SafeNormal( ConvertVector( normals.GetVec3( c.corner, c.ctrl, polyIdx ), yUp ) * invScale, faceNormal );
		}

		Vector2 uv0 = Vector2.Zero, uv1 = Vector2.Zero, uv2 = Vector2.Zero;
		if ( uvs != null )
		{
			uv0 = FlipV( uvs.GetVec2( a.corner, a.ctrl, polyIdx ) );
			uv1 = FlipV( uvs.GetVec2( b.corner, b.ctrl, polyIdx ) );
			uv2 = FlipV( uvs.GetVec2( c.corner, c.ctrl, polyIdx ) );
		}

		// per-face tangent from the UV gradient
		var tangent = Vector3.Zero;
		var w = 1f;
		var duv1 = uv1 - uv0;
		var duv2 = uv2 - uv0;
		var det = duv1.x * duv2.y - duv2.x * duv1.y;
		if ( MathF.Abs( det ) > 1e-12f )
		{
			var r = 1f / det;
			var e1 = p1 - p0;
			var e2 = p2 - p0;
			tangent = (e1 * duv2.y - e2 * duv1.y) * r;
			var bitangent = (e2 * duv1.x - e1 * duv2.x) * r;
			if ( Vector3.Dot( Vector3.Cross( faceNormal, tangent ), bitangent ) < 0 )
				w = -1f;
		}

		sub.Vertices.Add( MakeVertex( p0, n0, uv0, tangent, w ) );
		sub.Vertices.Add( MakeVertex( p1, n1, uv1, tangent, w ) );
		sub.Vertices.Add( MakeVertex( p2, n2, uv2, tangent, w ) );
		sub.Grow( p0 ); sub.Grow( p1 ); sub.Grow( p2 );

		piece.CollisionIndices.Add( a.ctrl );
		piece.CollisionIndices.Add( b.ctrl );
		piece.CollisionIndices.Add( c.ctrl );
	}

	static Vector2 FlipV( Vector2 uv ) => new( uv.x, 1f - uv.y ); // FBX UV origin is bottom-left, Source 2 is top-left

	static Vector3 SafeNormal( Vector3 n, Vector3 fallback )
		=> n.LengthSquared > 1e-12f ? n.Normal : fallback;

	static Vertex MakeVertex( Vector3 pos, Vector3 normal, Vector2 uv, Vector3 faceTangent, float w )
	{
		// orthonormalize the face tangent against this corner's normal
		var t = faceTangent - normal * Vector3.Dot( normal, faceTangent );
		if ( t.LengthSquared < 1e-12f )
		{
			t = Vector3.Cross( normal, Vector3.Up );
			if ( t.LengthSquared < 1e-6f )
				t = Vector3.Cross( normal, Vector3.Forward );
			w = 1f;
		}
		t = t.Normal;

		return new Vertex
		{
			Position = pos,
			Normal = normal,
			Tangent = new Vector4( t.x, t.y, t.z, w ),
			TexCoord0 = new Vector4( uv.x, uv.y, 0, 0 ),
			Color = Color32.White,
		};
	}
}
