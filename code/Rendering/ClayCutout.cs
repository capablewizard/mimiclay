using System;

namespace Mimiclay;

/// <summary>
/// Driver for the camera-occlusion cutout (the Baldur's Gate 3 "peek hole") — see Assets/shaders/clay_cutout.hlsl.
/// World-space version: the shader carves a noise-eroded 3D tunnel (capsule) along the camera → prop sight
/// line, so all this driver publishes is the two endpoints — prop centre + world radius, camera position +
/// cut distance — as scene-global render attributes. No screen projection, no physics trace (the shader
/// measures distance to the segment itself, which is also what handles several occluders along the line).
///
/// Any material on a shader that includes clay_cutout.hlsl (clay_world, sdf_mesh) then discards fragments
/// inside the tunnel that sit NEARER the camera than the prop — scenery between you and your disguise
/// dissolves open, with the noise anchored in world space so the hole reads as a physical opening. Materials
/// left on engine shaders (complex etc.) never cut; participation is per-material by construction.
///
/// This is LOCAL-ONLY render state: nothing networks, and the hunter's first-person view never calls in.
/// Scene.RenderAttributes is scene-global and sticky, so every path that stops driving the camera MUST
/// <see cref="Clear"/> (HiderController does so on release/stop-control/destroy/free-cam) — a stale hole
/// otherwise sticks to the screen.
/// </summary>
public static class ClayCutout
{
	/// <summary>Scene-wide kill switch (per-material opt-out is the shader's own "Camera Cutout" checkbox).
	/// All the LOOK tuning lives on <see cref="ClayCutoutSettings"/> — inspector sliders on the hider pawn,
	/// re-published every frame so drags show live in play mode.</summary>
	[ConVar( "mimiclay_cutout" )]
	public static bool Enabled { get; set; } = true;

	// Eased world radius, plus the lag history for each end of the tunnel. null = no history, snap on the
	// next frame. Plain statics are fine across Stop→Play: the first Update re-eases from whatever's here
	// and Clear resets them all — no SessionResetSystem hook needed.
	static float _radius;
	static Vector3? _lagCenter;
	static Vector3? _lagCam;
	static Vector3 _centerVel;
	static Vector3 _camVel;

	/// <summary>Drive the tunnel for this frame from the local player's prop bounds (world space). Call every
	/// frame while a third-person camera is looking at an ownable prop; call <see cref="Clear"/> when it stops.</summary>
	public static void Update( Scene scene, CameraComponent cam, Vector3 centerWs, float radiusWs, ClayCutoutSettings s )
	{
		if ( scene is null || !cam.IsValid() || !s.IsValid() )
			return;

		if ( !Enabled )
		{
			Clear( scene );
			return;
		}

		// Ease the radius open — the hole GROWS in rather than popping (and shrinks away on Clear-less
		// target changes, e.g. the disguise being resculpted smaller).
		float target = MathF.Max( radiusWs * s.RadiusScale, s.MinRadius );
		_radius += (target - _radius) * (1f - MathF.Exp( -s.EaseSpeed * Time.Delta ));

		// Cut margin: how far in FRONT of the prop centre (along the sight line) cutting stops, in world
		// units. From the FULL prop radius, never the eased one. The prop itself is protected by IDENTITY
		// (the exemption attribute HiderController stamps on its renderers), not by this margin — which is
		// what lets the margin go small so scenery hugging the prop still opens.
		var camPos = cam.WorldPosition;
		float cutMargin = radiusWs * s.CutSlack;

		// Positional lag: the hole trails and settles instead of being welded to the prop. BOTH ends of the
		// tunnel lag — the prop end gives the walk feel, the camera end is the only thing that moves while
		// ORBITING (the rig pins the pivot to the prop, so the prop centre is world-static there). The TRUE
		// camera position is still published separately (the shader's secondary-view gate needs it; fast
		// motion pushes the lagged one well past that gate's tolerance). Both are time constants in seconds,
		// so the feel is frame-rate independent. Sullivan gets this from a delayed spherecast (stepped);
		// smoothing the positions directly is continuous.

		// The camera end gets its own, longer time constant. Orbit lag is geometrically WEAKER than walk lag:
		// the tunnel's far end is pinned to the prop, so the hole's displacement on an occluder is only
		// (occluder's distance from the prop) × orbit angular velocity × lag time — vanishing for anything
		// you're hiding right behind. OrbitLagScale buys that displacement back without touching the walk
		// feel, which the prop-end lag owns.
		//
		// Do NOT be tempted to lag the sight DIRECTION to strengthen this: it leads instead of lagging. The
		// aim always points at the prop, so orbiting counter-clockwise rotates the aim counter-clockwise,
		// while fixed world features sweep the OTHER way across the screen — "where the camera aimed a moment
		// ago" and "where the world was a moment ago" are opposite for an orbit about a fixed point. Lagging
		// the camera POSITION is the honest version: the tunnel as it genuinely stood a moment ago.
		//
		// Smoothing is a critically damped SPRING (SmoothDamp), not a first-order exp lerp: exp smoothing has
		// a velocity discontinuity — the hole's speed jumps the instant the input flicks, which reads as
		// rubber-banding. The spring carries velocity as state, so motion eases in AND out with no kinks.
		float camLag = s.LagTime * MathF.Max( s.OrbitLagScale, 0.01f );

		// A respawn/teleport shouldn't send the hole gliding across the map — snap and drop the momentum.
		if ( _lagCenter is { } prevC && Vector3.DistanceBetween( prevC, centerWs ) > 500f )
			Clear( scene );

		var holeCenter = _lagCenter is { } pc
			? SpringTo( pc, ref _centerVel, centerWs, s.LagTime, Time.Delta ) : centerWs;
		var holeOrigin = _lagCam is { } po
			? SpringTo( po, ref _camVel, camPos, camLag, Time.Delta ) : camPos;
		_lagCenter = holeCenter;
		_lagCam = holeOrigin;

		// Both modes are carved from the SAME lagged world segment with the same world-anchored noise — the
		// 2D disc is just a cone (radius growing linearly from the origin, so constant screen size), which
		// the shader selects via the mode flag. Nothing screen-space needs computing here: no projection, no
		// scroll integration — the world anchoring gives 2D the exact lag/crawl behaviour of 3D for free.
		bool flat = s.Mode == ClayCutoutMode.Disc2D;

		scene.RenderAttributes.Set( "ClayCutoutHole", new Vector4( holeCenter.x, holeCenter.y, holeCenter.z, _radius ) );
		scene.RenderAttributes.Set( "ClayCutoutOrigin", new Vector4( holeOrigin.x, holeOrigin.y, holeOrigin.z,
			MathF.Tan( s.TunnelCone.Clamp( 0f, 89f ) * (MathF.PI / 180f) ) ) );
		scene.RenderAttributes.Set( "ClayCutoutCam", new Vector4( camPos.x, camPos.y, camPos.z, cutMargin ) );
		scene.RenderAttributes.Set( "ClayCutoutStyle", new Vector4( s.NoiseScale, s.NoiseScaleFine, s.Erode, s.RimWidth ) );
		scene.RenderAttributes.Set( "ClayCutoutScreen",
			new Vector4( s.OrbitNoiseDrive, s.OutlineWidth, s.DepthTaper, flat ? 1f : 0f ) );
		scene.RenderAttributes.Set( "ClayCutoutOutline",
			new Vector4( s.OutlineColor.r, s.OutlineColor.g, s.OutlineColor.b, s.OutlineColor.a ) );
		scene.RenderAttributes.Set( "ClayCutoutGuard",
			new Vector4( s.GroundGuard ? 1f : 0f, s.GroundGuardHeight, s.GroundGuardSlope, s.EdgeFeather ) );
		scene.RenderAttributes.Set( "ClayCutoutRimDarken", s.RimDarken );
	}

	/// <summary>Critically damped spring step (Unity's SmoothDamp / Game Programming Gems 4): moves current
	/// toward target over roughly smoothTime with velocity carried between frames as ref state — C1-smooth,
	/// no overshoot. The rational polynomial approximates e^-x well over a frame's worth of x and keeps the
	/// step stable at any dt (huge x → factor → 0 → output snaps to target).</summary>
	static Vector3 SpringTo( Vector3 current, ref Vector3 velocity, Vector3 target, float smoothTime, float dt )
	{
		float omega = 2f / MathF.Max( smoothTime, 0.0001f );
		float x = omega * dt;
		float e = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
		var change = current - target;
		var temp = (velocity + change * omega) * dt;
		velocity = (velocity - temp * omega) * e;
		return target + (change + temp) * e;
	}

	/// <summary>Shut the hole immediately. Radius ≤ 0 is the shader's disable signal. Also drops the lag
	/// history, so the next open snaps to the prop rather than gliding in from wherever it last was.</summary>
	public static void Clear( Scene scene )
	{
		_radius = 0f;
		_lagCenter = null;
		_lagCam = null;
		_centerVel = Vector3.Zero;
		_camVel = Vector3.Zero;
		scene?.RenderAttributes.Set( "ClayCutoutHole", new Vector4( 0f, 0f, 0f, -1f ) );
	}
}
