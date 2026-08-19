//=========================================================================================================================
// THE analytic SDF evaluator (LOCAL/model space). The Dreams "evaluator / CS of doom": evaluate the packed brush list
// at a local point. Included by EVERY consumer — the field-baking compute shaders (sdf_field_cs dense volume,
// sdf_atlas_fill_cs sparse atlas tiles, sdf_brick_classify_cs brick occupancy) AND sdf_raymarch.shader itself, which
// folds each world-space march sample into the prop's local frame (SdfDistWs) and evaluates here. One evaluator, no
// copies to drift: a new primitive/property is added HERE and every consumer picks it up. Brushes are packed in the
// prop's LOCAL space (identity transform) so the result is placement-invariant.
//
// Requires the including shader to have already done `#include "system.fxc"` (common/shared or common/pixel pull it in).
// The including shader declares its own OUTPUT (RWTexture etc.) and MainCs/MainPs; this file owns the brush INPUTS and
// the distance math.
//=========================================================================================================================
#ifndef MIMICLAY_SDF_EVAL_H
#define MIMICLAY_SDF_EVAL_H

Texture2D g_tBrushData < Attribute( "BrushData" ); >;  // LOCAL-space packed brushes (7 texels each)
Texture2D g_tSplineData< Attribute( "SplineData" ); >; // LOCAL spline control points: xyz pos, w radius
Texture2D g_tTextSdf   < Attribute( "TextSdf" ); >;    // baked text distance fields (R32F, MaxSlots stacked slots)
SamplerState g_sTextSdf < Filter( BILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;
int   g_nBrushCount < Attribute( "BrushCount" ); Default( 0 ); >;
int   g_nSdfCull    < Attribute( "SdfCull" ); Default( 1 ); >;  // per-brush AABB early-out (local AABB in slots 5/6)

#define TEXELS_PER_BRUSH 7 // must match SdfRaymarchRenderer.TexelsPerBrush
float4 LoadBrush( int k, int slot ) { return g_tBrushData.Load( int3( k * TEXELS_PER_BRUSH + slot, 0, 0 ) ); }
float4 LoadSplinePoint( int i )     { return g_tSplineData.Load( int3( i, 0, 0 ) ); }

// ── Primitives ──
#define MAX_SLICE 0.95 // must match SdfBrush.MaxSlice (deepest cut still leaves a sliver)

float sdSphere( float3 p, float r ) { return length( p ) - r; }

float sdEllipsoid( float3 p, float3 r )
{
	r = max( r, 1e-3 );
	float k0 = length( p / r );
	float k1 = length( p / (r * r) );
	return k1 > 1e-6 ? k0 * (k0 - 1.0) / k1 : -min( r.x, min( r.y, r.z ) );
}

float sdBox( float3 p, float3 b, float r )
{
	r = max( 0.0, min( r, min( b.x, min( b.y, b.z ) ) ) );
	float3 q = abs( p ) - b + r;
	return length( max( q, 0.0 ) ) + min( max( q.x, max( q.y, q.z ) ), 0.0 ) - r;
}

float sdCylinder( float3 p, float rad, float h, float r )
{
	r = max( 0.0, min( r, min( rad, h ) ) );
	float dx = length( p.xy ) - (rad - r);
	float dz = abs( p.z ) - (h - r);
	return min( max( dx, dz ), 0.0 ) + length( max( float2( dx, dz ), 0.0 ) ) - r;
}

float cappedCone( float qx, float qy, float h, float r1, float r2 )
{
	float2 k2 = float2( r2 - r1, 2.0 * h );
	float2 ca = float2( qx - min( qx, (qy < 0.0) ? r1 : r2 ), abs( qy ) - h );
	float t = clamp( dot( float2( r2 - qx, h - qy ), k2 ) / dot( k2, k2 ), 0.0, 1.0 );
	float2 cb = float2( qx - r2, qy - h ) + k2 * t;
	float s = (cb.x < 0.0 && ca.y < 0.0) ? -1.0 : 1.0;
	return s * sqrt( min( dot( ca, ca ), dot( cb, cb ) ) );
}

// Cone (base radius R at z=-H, apex at +H), optionally SLICED flat by the slice fraction — the slice is
// part of the primitive (a frustum), so ONE Minkowski opening rounds every edge together: erode all faces
// (slant, base, cut) by r, exact capped-cone distance of the eroded trapezoid, dilate by r. The cut rim
// rounds exactly like the base rim and the flat survives any legal r. Unsliced (Rt = 0) this reproduces
// the classic pointed-cone formula exactly. Matches SdfBrush.ConeDistance.
float sdCone( float3 p, float R, float H, float rounding, float slice )
{
	float qx = length( p.xy );

	float s = clamp( slice, 0.0, MAX_SLICE );
	float zcut = H * (1.0 - 2.0 * s); // top cap height (= +H when unsliced)
	float Rt = R * s;                 // top cap radius (0 = pointed)
	float hh = (zcut + H) * 0.5;      // frustum half-height
	float zc0 = (zcut - H) * 0.5;     // frustum centre

	if ( hh < 1e-4 || R < 1e-4 )
		return length( float2( qx, p.z - zc0 ) ) - max( R, max( Rt, 1e-3 ) );

	float L = sqrt( (R - Rt) * (R - Rt) + 4.0 * hh * hh ); // slant length
	float k = (Rt - R) / (2.0 * hh);                       // slant slope dq/dz (< 0)
	// Erosion limit: caps can't cross, eroded base radius can't go negative (= inradius when unsliced).
	float r = clamp( rounding, 0.0, min( hh, 2.0 * hh * R / max( L + R - Rt, 1e-4 ) ) );

	if ( r < 1e-4 )
		return cappedCone( qx, p.z - zc0, hh, R, Rt );

	// Erode: caps move in by r; the slant shifts horizontally by r/cos(tilt) = r·L/(2hh).
	float dq = r * L / (2.0 * hh);
	float zb = -H + r, zt = zcut - r;
	float r1 = R + r * k - dq;  // eroded radius at the bottom cap
	float r2 = Rt - r * k - dq; // eroded radius at the top cap

	if ( r2 < 0.0 )
	{
		zt = (dq - R) / k - H; // slant meets the axis below the top cap — eroded shape is pointed
		r2 = 0.0;              // (the classic zApex = H − r/sinB when unsliced)
	}

	if ( zt <= zb ) // caps met — eroded core is a flat disc of radius r1 == r2 (0 when unsliced → ball)
	{
		float rd = max( 0.5 * (r1 + r2), 0.0 );
		float2 w = float2( max( qx - rd, 0.0 ), p.z - (zb + zt) * 0.5 );
		return length( w ) - r;
	}

	return cappedCone( qx, p.z - (zb + zt) * 0.5, (zt - zb) * 0.5, r1, r2 ) - r;
}

float sdTriangle2D( float2 p, float2 p0, float2 p1, float2 p2 )
{
	float2 e0 = p1 - p0, e1 = p2 - p1, e2 = p0 - p2;
	float2 w0 = p - p0, w1 = p - p1, w2 = p - p2;
	float2 pq0 = w0 - e0 * clamp( dot( w0, e0 ) / dot( e0, e0 ), 0.0, 1.0 );
	float2 pq1 = w1 - e1 * clamp( dot( w1, e1 ) / dot( e1, e1 ), 0.0, 1.0 );
	float2 pq2 = w2 - e2 * clamp( dot( w2, e2 ) / dot( e2, e2 ), 0.0, 1.0 );
	float s = sign( e0.x * e2.y - e0.y * e2.x );
	float2 d = min( min( float2( dot( pq0, pq0 ), s * (w0.x * e0.y - w0.y * e0.x) ),
	                     float2( dot( pq1, pq1 ), s * (w1.x * e1.y - w1.y * e1.x) ) ),
	                     float2( dot( pq2, pq2 ), s * (w2.x * e2.y - w2.y * e2.x) ) );
	return -sqrt( d.x ) * sign( d.y );
}

float triInradius( float2 a, float2 b, float2 c )
{
	float per = length( a - b ) + length( b - c ) + length( c - a );
	float area = abs( (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y) ) * 0.5;
	return per > 1e-5 ? 2.0 * area / per : 0.0;
}

float2 insetVertex( float2 vi, float2 n1, float2 n2, float r )
{
	float2 a = normalize( n1 - vi ), b = normalize( n2 - vi );
	float2 bis = a + b;
	float bl = length( bis );
	if ( bl < 1e-5 ) return vi;
	bis /= bl;
	float sinHalf = sqrt( max( (1.0 - dot( a, b )) * 0.5, 1e-6 ) );
	return vi + bis * (r / sinHalf);
}

float sdTriPrism( float3 p, float wx, float hy, float hz, float rounding )
{
	wx = max( wx, 1e-3 ); hy = max( hy, 1e-3 );
	float2 v0 = float2( 0.0, hy ), v1 = float2( -wx, -hy ), v2 = float2( wx, -hy );
	float r = clamp( rounding, 0.0, min( triInradius( v0, v1, v2 ), hz ) );
	if ( r > 1e-4 )
	{
		float2 i0 = insetVertex( v0, v1, v2, r ), i1 = insetVertex( v1, v0, v2, r ), i2 = insetVertex( v2, v0, v1, r );
		v0 = i0; v1 = i1; v2 = i2;
	}
	float d2 = sdTriangle2D( p.xy, v0, v1, v2 );
	float dz = abs( p.z ) - (hz - r);
	return min( max( d2, dz ), 0.0 ) + length( max( float2( d2, dz ), 0.0 ) ) - r;
}

// IQ exact regular hexagon, a = apothem (centre to flat edge). Vertices on ±X (must match
// SdfBrush.Hexagon2D / CrossSectionOutline).
float sdHexagon2D( float2 p, float a )
{
	const float3 k = float3( -0.86602540, 0.5, 0.57735027 );
	p = abs( p );
	p -= 2.0 * min( dot( k.xy, p ), 0.0 ) * k.xy;
	p -= float2( clamp( p.x, -k.z * a, k.z * a ), a );
	return length( p ) * sign( p.y );
}

// IQ exact regular pentagon, a = apothem, one vertex pointing +Y (IQ's original is flat-top/vertex-down,
// hence the Y negate) — must match SdfBrush.Pentagon2D / CrossSectionOutline.
float sdPentagon2D( float2 p, float a )
{
	const float3 k = float3( 0.80901699, 0.58778525, 0.72654253 ); // cos36, sin36, tan36
	p.y = -p.y; // vertex-up convention
	p.x = abs( p.x );
	p -= 2.0 * min( dot( float2( -k.x, k.y ), p ), 0.0 ) * float2( -k.x, k.y );
	p -= 2.0 * min( dot( float2( k.x, k.y ), p ), 0.0 ) * float2( k.x, k.y );
	p -= float2( clamp( p.x, -k.z * a, k.z * a ), a );
	return length( p ) * sign( p.y );
}

// IQ exact 5-point star (sdStar, n = 5, m = 3), R = tip circumradius, one tip along +Y (must match
// SdfBrush.Star2D / CrossSectionOutline).
float sdStar2D( float2 p, float R )
{
	const float an = 0.62831853;                        // PI/5
	const float2 acs = float2( 0.80901699, 0.58778525 ); // (cos, sin) PI/5
	const float2 ecs = float2( 0.5, 0.86602540 );        // (cos, sin) PI/3 — the edge direction
	float bn = atan2( p.x, p.y );
	bn -= 2.0 * an * floor( bn / (2.0 * an) );           // positive mod (fmod mirrors negatives)
	bn -= an;
	p = length( p ) * float2( cos( bn ), abs( sin( bn ) ) );
	p -= R * acs;
	p += ecs * clamp( -dot( p, ecs ), 0.0, R * acs.y / ecs.y );
	return length( p ) * sign( p.x );
}

#define STAR_EDGE_FACTOR 0.40673664 // sin(60°−36°): star edge-to-centre / circumradius (SdfBrush.StarEdgeFactor)

// Extruded profile (shape 4): triangle (xs 0), 5-point star (1), regular hexagon (2). Triangle keeps the
// isosceles wx/hy params; star/hexagon are regular with circumradius wx. Rounding = erode the profile then
// dilate (Minkowski opening), so it stays inside the sharp silhouette — matches SdfBrush.ExtrudedDistance.
float sdExtruded( float3 p, float wx, float hy, float hz, float rounding, int xs )
{
	if ( xs == 1 )
	{
		float R = max( wx, 1e-3 );
		float r = clamp( rounding, 0.0, min( R * STAR_EDGE_FACTOR, hz ) );
		float d2 = sdStar2D( p.xy, R - r / STAR_EDGE_FACTOR ); // uniform scale = exact all-edges inset
		float dz = abs( p.z ) - (hz - r);
		return min( max( d2, dz ), 0.0 ) + length( max( float2( d2, dz ), 0.0 ) ) - r;
	}
	if ( xs == 2 )
	{
		float a = max( wx, 1e-3 ) * 0.86602540; // apothem from circumradius
		float r = clamp( rounding, 0.0, min( a, hz ) );
		float d2 = sdHexagon2D( p.xy, a - r );
		float dz = abs( p.z ) - (hz - r);
		return min( max( d2, dz ), 0.0 ) + length( max( float2( d2, dz ), 0.0 ) ) - r;
	}
	if ( xs == 3 )
	{
		float a = max( wx, 1e-3 ) * 0.80901699; // apothem from circumradius
		float r = clamp( rounding, 0.0, min( a, hz ) );
		float d2 = sdPentagon2D( p.xy, a - r );
		float dz = abs( p.z ) - (hz - r);
		return min( max( d2, dz ), 0.0 ) + length( max( float2( d2, dz ), 0.0 ) ) - r;
	}
	return sdTriPrism( p, wx, hy, hz, rounding );
}

// Planar slice for the sphere (the cone folds its slice into sdCone): intersect with the half-space below
// z = halfH·(1 − 2·slice), the rim rounded by the Round slider (erode shape + plane by r, rounded-corner
// join, dilate by r — the same combine the extrusions use). Matches SdfBrush.SliceRound. Callers pre-clamp
// r so the fillet fits the cut disc (see SdfShapeDist).
float sliceRound( float d, float z, float halfH, float slice, float rounding )
{
	if ( slice <= 0.0 ) return d;
	float zcut = halfH * (1.0 - 2.0 * min( slice, MAX_SLICE ));
	float dz = z - zcut;
	float r = clamp( rounding, 0.0, (zcut + halfH) * 0.5 ); // ≤ half the remaining thickness
	if ( r <= 1e-3 ) return max( d, dz ); // hard slice
	float2 w = float2( d, dz ) + r;
	return min( max( w.x, w.y ), 0.0 ) + length( max( w, 0.0 ) ) - r;
}

// Extruded TEXT: the 2D profile is the brush's baked distance field (one atlas slot, texel units), sampled
// at the local XY and pushed through the same rounded extrusion as the analytic profiles. Outside the text
// quad the clamped sample is repaired with the quad distance (both are lower bounds on the true glyph
// distance; max keeps marching safe). Round insets the glyphs — a real distance field, so letter edges and
// corners round like clay. Matches SdfBrush.TextDistance.
#define TEXT_SLOT_W 256.0 // must match SdfTextData.Width/Height and SdfTextSdf.MaxSlots
#define TEXT_SLOT_H 128.0
#define TEXT_SLOTS  8.0

float sdText( float3 p, float3 he, int slot, float rounding )
{
	float2 h2 = max( he.xy, 1e-3 );
	float2 uv = saturate( p.xy / (2.0 * h2) + 0.5 );
	uv.y = 1.0 - uv.y; // bitmap rows run top-down

	// Half-texel-inset remap into this brush's atlas slot, so bilinear never bleeds into a neighbour slot.
	float2 st = float2( (uv.x * (TEXT_SLOT_W - 1.0) + 0.5) / TEXT_SLOT_W,
	                    ((uv.y * (TEXT_SLOT_H - 1.0) + 0.5) / TEXT_SLOT_H + slot) / TEXT_SLOTS );
	float ts = min( 2.0 * h2.x / TEXT_SLOT_W, 2.0 * h2.y / TEXT_SLOT_H ); // world per texel (conservative)
	float d2 = g_tTextSdf.SampleLevel( g_sTextSdf, st, 0 ).r * ts;

	// Outside the quad there's no field data (the sample is edge-clamped). Nearest glyph is inward of the
	// clamp point while p is outward of it, so those legs can't oppose: d_true² ≥ dq² + sample². This
	// Pythagorean bound is tight and SMOOTH across the rim — a cruder max() bound kinks the field along the
	// quad rectangle, which reads as a crease wherever the rim blends with nearby clay.
	float2 q = abs( p.xy ) - h2;
	float dq = length( max( q, 0.0 ) );
	if ( dq > 0.0 )
	{
		float d2o = max( d2, 0.0 );
		d2 = sqrt( dq * dq + d2o * d2o );
	}

	float hz = max( he.z, 1e-3 );
	float r = clamp( rounding, 0.0, hz );
	float d2e = d2 + r; // erode the glyphs; the -r below dilates — rounds every letter edge
	float dz = abs( p.z ) - (hz - r);
	return min( max( d2e, dz ), 0.0 ) + length( max( float2( d2e, dz ), 0.0 ) ) - r;
}

float SdfShapeDist( float shapeId, float3 pl, float4 B, float rr, int xs, float slice )
{
	int shape = (int)(shapeId + 0.5);
	if ( shape == 1 ) return sdBox( pl, B.xyz, rr );
	if ( shape == 2 ) return sdCylinder( pl, B.x, B.z, rr );
	if ( shape == 3 )
	{
		pl.z -= B.z; // base-pivot cone: Position sits on the base; shift into the centred cone frame
		return sdCone( pl, B.x, B.z, rr, slice ); // slice is part of the primitive (rounded frustum)
	}
	if ( shape == 4 ) return sdExtruded( pl, B.x, B.y, B.z, rr, xs );
	if ( shape == 5 ) return 1e9; // spline handled in OneBrushDist
	if ( shape == 6 ) return sdText( pl, B.xyz, xs, rr ); // xs lane doubles as the text atlas slot
	// Sphere/ellipsoid: rim fillet capped by the CAP DEPTH (material under the cut plane, 2·rz·slice) and
	// the shape's radii — past the depth the fillet consumes the flat and the top sinks below the cut. At
	// the limit the slice rounds into a smooth dome kissing the plane (SdfBrush.SphereSliceMaxRounding).
	float ss = min( max( slice, 0.0 ), MAX_SLICE );
	float rs = min( rr, min( min( B.x, min( B.y, B.z ) ), 2.0 * max( B.z, 1e-3 ) * ss ) );
	return sliceRound( sdEllipsoid( pl, B.xyz ), pl.z, max( B.z, 1e-3 ), slice, rs );
}

float smin( float a, float b, float k )
{
	if ( k <= 0.0 ) return min( a, b );
	float h = saturate( 0.5 + 0.5 * (b - a) / k );
	return lerp( b, a, h ) - k * h * (1.0 - h);
}

float ssub( float d, float s, float k )
{
	if ( k <= 0.0 ) return max( d, -s );
	float h = saturate( 0.5 - 0.5 * (d + s) / k );
	return lerp( d, -s, h ) + k * h * (1.0 - h);
}

float3 qrot( float4 q, float3 v ) { return v + 2.0 * cross( q.xyz, cross( q.xyz, v ) + q.w * v ); }

float sdAabb( float3 p, float3 mn, float3 mx )
{
	float3 c = (mn + mx) * 0.5, e = (mx - mn) * 0.5;
	float3 q = abs( p - c ) - e;
	return length( max( q, 0.0 ) ) + min( max( q.x, max( q.y, q.z ) ), 0.0 );
}

// ── Spline (variable-radius tube), evaluated in local space ──
float sdRoundCone( float3 p, float4 a, float4 b )
{
	float3 ba = b.xyz - a.xyz;
	float l2 = dot( ba, ba );
	if ( l2 < 1e-6 ) return length( p - a.xyz ) - max( a.w, b.w );

	float r1 = a.w, r2 = b.w;
	float rr = r1 - r2;
	float a2 = l2 - rr * rr;
	float il2 = 1.0 / l2;

	float3 pa = p - a.xyz;
	float y = dot( pa, ba );
	float z = y - l2;
	float3 xv = pa * l2 - ba * y;
	float x2 = dot( xv, xv );
	float y2 = y * y * l2;
	float z2 = z * z * l2;

	float k = sign( rr ) * rr * rr * x2;
	if ( sign( z ) * a2 * z2 > k ) return sqrt( x2 + z2 ) * il2 - r2;
	if ( sign( y ) * a2 * y2 < k ) return sqrt( x2 + y2 ) * il2 - r1;
	return ( sqrt( x2 * a2 * il2 ) + y * rr ) * il2 - r1;
}

#define SPLINE_TESS 8

float4 CatmullRomPoint( float4 p0, float4 p1, float4 p2, float4 p3, float t, float curv )
{
	float t2 = t * t, t3 = t2 * t;
	float h00 = 2.0 * t3 - 3.0 * t2 + 1.0;
	float h10 = t3 - 2.0 * t2 + t;
	float h01 = -2.0 * t3 + 3.0 * t2;
	float h11 = t3 - t2;
	float4 m1 = (p2 - p0) * (0.5 * curv);
	float4 m2 = (p3 - p1) * (0.5 * curv);
	float4 r = h00 * p1 + h10 * m1 + h01 * p2 + h11 * m2;
	r.w = max( r.w, 0.1 );
	return r;
}

float SplineDist( float3 p, int off, int count, float curv, bool closed )
{
	if ( count < 1 ) return 1e9;
	if ( count == 1 ) { float4 a = LoadSplinePoint( off ); return length( p - a.xyz ) - a.w; }

	bool loop = closed && count >= 3;
	int segs = loop ? count : count - 1;
	int k = curv <= 1e-4 ? 1 : SPLINE_TESS;
	float d = 1e9;
	[loop] for ( int i = 0; i < segs; i++ )
	{
		int j0 = loop ? (i - 1 + count) % count : (i > 0 ? i - 1 : 0);
		int j2 = loop ? (i + 1) % count : i + 1;
		int j3 = loop ? (i + 2) % count : (i < count - 2 ? i + 2 : count - 1);
		float4 p0 = LoadSplinePoint( off + j0 );
		float4 p1 = LoadSplinePoint( off + i );
		float4 p2 = LoadSplinePoint( off + j2 );
		float4 p3 = LoadSplinePoint( off + j3 );

		if ( g_nSdfCull != 0 )
		{
			float3 sc = (p1.xyz + p2.xyz) * 0.5;
			float sr = 0.5 * length( p2.xyz - p1.xyz ) + max( p1.w, p2.w );
			if ( curv > 1e-4 )
			{
				sr += 0.5 * curv * max( length( p2.xyz - p0.xyz ), length( p3.xyz - p1.xyz ) );
				sr += 0.5 * curv * max( abs( p2.w - p0.w ), abs( p3.w - p1.w ) );
			}
			if ( length( p - sc ) - sr >= d )
				continue;
		}

		float4 prev = p1;
		[loop] for ( int s = 1; s <= k; s++ )
		{
			float4 cur = CatmullRomPoint( p0, p1, p2, p3, s / (float)k, curv );
			d = min( d, sdRoundCone( p, prev, cur ) );
			prev = cur;
		}
	}
	return d;
}

// Bare distance to one brush at LOCAL point lp. Spline reads its control-point pool; every other shape is
// transformed into the brush's local frame first. xs = extruded cross-section id (slot 5 .w); slice = planar
// top-slice fraction (slot 6 .w).
float OneBrushDist( float3 lp, float4 A, float4 B, float4 C, float rr, int xs, float slice )
{
	if ( (int)(A.w + 0.5) == 5 )
		return SplineDist( lp, (int)(B.x + 0.5), (int)(B.y + 0.5), B.z, rr > 0.5 );
	return SdfShapeDist( A.w, qrot( float4( -C.xyz, C.w ), lp - A.xyz ), B, rr, xs, slice );
}

// Distance to one brush including mirror copies. LOCAL space, so mirroring is a direct reflection of the
// sample point across the sculpture-origin planes (no world<->model fold like the raymarch needs).
float BrushDist( float3 lp, float4 A, float4 B, float4 C, float rr, int mask, int xs, float slice )
{
	float bd = OneBrushDist( lp, A, B, C, rr, xs, slice );
	if ( mask == 0 )
		return bd;

	int nx = (mask & 1) ? 1 : 0;
	int ny = (mask & 2) ? 1 : 0;
	int nz = (mask & 4) ? 1 : 0;
	[loop] for ( int sx = 0; sx <= nx; sx++ )
	[loop] for ( int sy = 0; sy <= ny; sy++ )
	[loop] for ( int sz = 0; sz <= nz; sz++ )
	{
		if ( sx + sy + sz == 0 )
			continue;
		float3 Lm = lp * float3( sx ? -1.0 : 1.0, sy ? -1.0 : 1.0, sz ? -1.0 : 1.0 );
		bd = smin( bd, OneBrushDist( Lm, A, B, C, rr, xs, slice ), B.w );
	}
	return bd;
}

// Combined field at LOCAL point lp — the analytic brush loop.
float SdfDist( float3 lp )
{
	float d = 1e9;
	[loop]
	for ( int k = 0; k < g_nBrushCount; k++ )
	{
		float4 A = LoadBrush( k, 0 );
		float4 B = LoadBrush( k, 1 );
		float4 C = LoadBrush( k, 2 );
		float4 D = LoadBrush( k, 3 );
		float4 E = LoadBrush( k, 4 );
		float4 F = LoadBrush( k, 5 ); // cull AABB min .xyz, extruded cross-section id .w
		float4 G = LoadBrush( k, 6 ); // cull AABB max .xyz, slice fraction .w

		// Cull threshold per op: add matters within blend of becoming the nearest (d + k), subtract while
		// material is within blend (k − d). Cutout carves the shell |bd| ≥ bd, so the subtract bound stays
		// conservative for it too.
		if ( g_nSdfCull != 0 && sdAabb( lp, F.xyz, G.xyz ) > (D.w < 0.5 ? d + B.w : B.w - d) )
			continue;

		float bd = BrushDist( lp, A, B, C, E.x, (int)(E.w + 0.5), (int)(F.w + 0.5), G.w );
		// Op (D.w): 0 add, 1 subtract, 2 cutout. Cutout subtracts a thin SHELL of the brush boundary (|bd|)
		// — a groove where the brush surface crosses the clay, sized by Blend (0 = geometric no-op; the
		// recolour half of the op lives in sdf_raymarch's SdfShade).
		d = (D.w < 0.5) ? smin( d, bd, B.w ) : ssub( d, (D.w < 1.5) ? bd : abs( bd ), B.w );
	}
	return d;
}

// ─── Plasticine displacement noise + the field-BAKE inputs ─────────────────────────────────────────
//
// The lumps are baked INTO the distance volume by the field/atlas compute shaders (re-dispatched per
// claymation-boil tick by SdfRaymarchRenderer), so the raymarch and the highlight get them for free —
// their one trilinear fetch per step — instead of paying the two-octave noise (~16 hash13) per march
// sample. These noise definitions used to live in sdf_raymarch's PS; they moved here so the baker and
// the live analytic fallback (D_DISPLACE while no field is ready) share ONE definition and the baked
// lumps match the live ones sample-for-sample.

float hash13( float3 p3 )
{
	p3 = frac( p3 * 0.1031 );
	p3 += dot( p3, p3.zyx + 31.32 );
	return frac( (p3.x + p3.y) * p3.z );
}

// Trilinearly-interpolated value noise, smoothstep-faded per cell. Output ~[0,1].
float vnoise( float3 x )
{
	float3 i = floor( x );
	float3 f = x - i;
	f = f * f * (3.0 - 2.0 * f);
	return lerp(
		lerp( lerp( hash13( i + float3( 0, 0, 0 ) ), hash13( i + float3( 1, 0, 0 ) ), f.x ),
		      lerp( hash13( i + float3( 0, 1, 0 ) ), hash13( i + float3( 1, 1, 0 ) ), f.x ), f.y ),
		lerp( lerp( hash13( i + float3( 0, 0, 1 ) ), hash13( i + float3( 1, 0, 1 ) ), f.x ),
		      lerp( hash13( i + float3( 0, 1, 1 ) ), hash13( i + float3( 1, 1, 1 ) ), f.x ), f.y ),
		f.z );
}

// A per-tick random offset in noise-cell units, centred on 0. Fed a QUANTISED tick, so it holds its
// value for a whole tick and then jumps — that discrete hold-then-pop is the claymation boil.
float3 BoilOffset( float tick )
{
	return float3( hash13( float3( tick, 17.13, 3.71 ) ),
	               hash13( float3( tick, 53.77, 8.29 ) ),
	               hash13( float3( tick, 91.31, 5.13 ) ) ) - 0.5;
}

// Bake-time displacement inputs, pushed by SdfFieldGpu per dispatch. Defaults = no displacement, so
// every OTHER consumer of this file (the raymarch material, the classify pass) never setting them is
// exactly "off". The tick is quantised on the CPU — floor(time·BoilFps) wrapped to 1024 ticks, plus
// the per-prop seed, mirroring the live path's in-shader tick — and < 0 means boil off.
float g_flBakeDispAmp       < Attribute( "BakeDispAmp" );       Default( 0.0 ); >;
float g_flBakeDispFreq      < Attribute( "BakeDispFreq" );      Default( 0.25 ); >;
float g_flBakeBoilTick      < Attribute( "BakeBoilTick" );      Default( -1.0 ); >;
float g_flBakeBoilJitter    < Attribute( "BakeBoilJitter" );    Default( 0.0 ); >;
float g_flBakeBoilAmpJitter < Attribute( "BakeBoilAmpJitter" ); Default( 0.0 ); >;

// Signed displacement at a LOCAL point — the same two-octave field sdf_raymarch's live fallback
// computes (see the long comment there for why each piece is shaped the way it is: separate octave
// offsets, offsets added after the freq multiply, amp wobble bounded by BoilAmpJitter).
float BakeDisplacement( float3 lp )
{
	float3 b0 = 0.0, b1 = 0.0;
	float amp = g_flBakeDispAmp;
	if ( g_flBakeBoilTick >= 0.0 )
	{
		b0 = BoilOffset( g_flBakeBoilTick )         * g_flBakeBoilJitter;
		b1 = BoilOffset( g_flBakeBoilTick + 101.0 ) * g_flBakeBoilJitter * 2.7;
		amp *= 1.0 + ( hash13( float3( g_flBakeBoilTick, 7.77, 1.23 ) ) - 0.5 ) * g_flBakeBoilAmpJitter;
	}
	float3 x = lp * g_flBakeDispFreq;
	float n = vnoise( x + b0 ) * 0.67 + vnoise( x * 2.03 + 11.7 + b1 ) * 0.33;
	return (n * 2.0 - 1.0) * amp;
}

// The value the field volume / atlas tiles store: the brush union minus the lumps. Amp 0 (the
// default, and every prop with Displace off) makes this exactly SdfDist.
float SdfDistBaked( float3 lp )
{
	float d = SdfDist( lp );
	if ( g_flBakeDispAmp > 0.0 )
		d -= BakeDisplacement( lp );
	return d;
}

#endif // MIMICLAY_SDF_EVAL_H
