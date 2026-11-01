using System;

namespace Mimiclay;

// The climb's control rules, shared by props (HiderController.Climb) and hunters (HunterClimb) so the keys mean the same
// thing on a wall whatever you are. Pure functions: no state, no traces.
public static class ClimbRules
{
	// WASD while climbing (user, 2026-10-29): A/D go SIDEWAYS along the surface, by the camera's left; W/S go in DEPTH —
	// the camera's level forward, kept only as far as the surface faces up or down: all of it on a ceiling (W = forward
	// along it), none on an upright wall, where W only pushes INTO the wall (Into — that's what mounts a ledge at the
	// top). Up and down a wall are Space and Ctrl. Pitch is ignored, so neither wobbles with the look or a bumpy face.
	//
	// `lookAlong` (hunters, user 2026-11-01): on a wall, W/S also go the way you LOOK along it — look down the wall, W walks
	// you along it. Only the look's share along the wall counts (see WallAlongness), so facing the wall W is still "into".
	public static Vector3 Along( float viewYaw, Vector3 move, Vector3 n, float speed, bool lookAlong = false )
	{
		if ( move.IsNearZeroLength )
			return Vector3.Zero;

		var view = Rotation.FromYaw( viewYaw );
		Vector3 Flat( Vector3 v ) => v - n * Vector3.Dot( v, n );

		var depth = Flat( view.Forward ) * MathF.Abs( n.z );
		if ( lookAlong )
		{
			var along = Flat( view.Forward );
			if ( !along.IsNearZeroLength )
				depth += along.Normal * WallAlongness( viewYaw, n ) * (1f - MathF.Abs( n.z ));
		}
		var side = Flat( view.Left );
		side = side.Length >= 0.3f ? side.Normal : side / 0.3f; // looking along the wall: A/D say little, so slow rather than snap

		return (depth * move.x + side * move.y).ClampLength( 1f ) * speed;
	}

	// How hard W/S push INTO the surface (0..1, S negative). Mounts a ledge. Fully in unless the level look points well away
	// from the surface: on an upright wall W has no other job (Along gives it no depth there), and taking only the look's
	// share pointing at the wall meant W did nothing at the top while looking along the wall — no mantle (user, 2026-11-01).
	//
	// `lookAlong`: W/S already move along the wall as far as the look runs along it, so only the rest pushes in — otherwise
	// S while looking down the wall (walking backwards along it) read as pulling away from it.
	public static float Into( float viewYaw, Vector3 move, Vector3 n, bool lookAlong = false )
	{
		var forward = Rotation.FromYaw( viewYaw ).Forward;
		float facing = Vector3.Dot( forward, -n );
		float into = move.x * (facing > -0.5f ? 1f : facing);
		return lookAlong ? into * (1f - WallAlongness( viewYaw, n )) : into;
	}

	// How far the level look runs ALONG a surface rather than at it: 0 looking at it (within ~45°), 1 looking along it
	// (within ~20°). The dead zone keeps W a clean "into the wall" (the mantle) when you're roughly facing it.
	public static float WallAlongness( float viewYaw, Vector3 n )
	{
		var flatN = n.WithZ( 0f );
		if ( flatN.IsNearZeroLength )
			return 0f;
		var forward = Rotation.FromYaw( viewYaw ).Forward;
		float sideways = MathF.Abs( Vector3.Cross( forward, flatN.Normal ).z ); // sin of the angle off the wall's normal
		return Math.Clamp( (sideways - 0.7f) / (0.94f - 0.7f), 0f, 1f );
	}

	// Part of a hunter pawn (its capsule, or the clay head's own kinematic body)? Nobody climbs a hunter — not another
	// hunter, not a prop (user, 2026-11-01).
	public static bool IsHunter( GameObject go )
	{
		for ( var g = go; g.IsValid(); g = g.Parent )
			if ( g.Components.Get<HunterController>().IsValid() )
				return true;
		return false;
	}

	// Space / Ctrl: up or down the surface. Under a ceiling there's no "up the surface", so Space carries on `ceilingOut`
	// (out from the wall we came up) and Ctrl comes back.
	public static Vector3 Vertical( float vertical, Vector3 n, Vector3 ceilingOut, float speed )
	{
		var up = Vector3.Up - n * Vector3.Dot( Vector3.Up, n );
		if ( up.Length > 0.2f )
			return up.Normal * speed * vertical;
		return ceilingOut * speed * vertical;
	}

	// Just round a corner with the same keys held: drop any motion back into the surface we left (`left` is its normal).
	// Otherwise Space (up the wall) and W (toward the wall, on the ceiling) passed the climber back and forth between wall
	// and ceiling every corner cooldown, never settling on either.
	public static Vector3 NotBack( Vector3 v, Vector3 left )
	{
		float back = Vector3.Dot( v, -left );
		return back > 0f ? v + left * back : v;
	}
}
