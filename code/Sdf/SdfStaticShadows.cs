using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Lets the engine's static shadow cache for spot/point lights hold SDF props that aren't changing (Round 2 #1b in
/// SDF-OPTIMISATION-PLAN.md). Off by default: <c>mimiclay_sdf_static_shadows 1</c>.
///
/// What the engine does (ShadowMapper, installed 2026-10-23 build): a spot/point light whose GameObject is static
/// renders every caster flagged <c>SceneObjectFlags.StaticObject</c> ONCE into a cache, then each frame copies the
/// cache and draws only the dynamic casters on top. The cache is dropped only when the light's SceneLight
/// <c>TransformVersion</c> changes (any Transform set that differs by more than 0.0001) or its shadow resolution
/// changes. Nothing watches the casters (the engine's caster-change hooks are empty stubs), and turning a light's
/// static flag OFF keeps the cache it already has. So whatever we mark static, we must invalidate ourselves.
///
/// How it's kept correct:
/// <list type="bullet">
/// <item>A renderer marks its scene object static only once its shadow inputs (shape, field, placement, casting
/// state, shadow knobs) have been unchanged for <see cref="SettleSeconds"/>, and never while it boils, renders into
/// another world or as a viewmodel (SdfRaymarchRenderer.UpdateStaticShadow). Anything that changes often (pawns,
/// held props, boiling props, live edits) therefore just stays dynamic.</item>
/// <item>Any change to a static caster drops it back to dynamic in the same frame, before rendering, and queues an
/// invalidation at the bounds it was cached at. So does disabling, destroying or recreating its scene object.</item>
/// <item>Promotions are batched to one window per <see cref="PromoteInterval"/>, so props settling one by one cost
/// at most one re-cache per light per window instead of one each.</item>
/// <item>Once per frame (FinishUpdate) every spot/point SceneLight whose range sphere reaches a queued bounds sphere
/// is nudged: its Transform is set off by 0.01 u and straight back, which bumps TransformVersion twice and leaves
/// the light where it was. The engine re-renders that light's cache on its next shadow pass. SceneLight is reached
/// through <c>SceneWorld.SceneObjects</c> because <c>Light.SceneObject</c> is internal; every spot/point light is
/// nudged, static or not, because a light that WAS static keeps its stale cache.</item>
/// </list>
/// The sun has no static cache in the engine, so this does nothing for cascades.
/// </summary>
public sealed class SdfStaticShadows : GameObjectSystem
{
	/// <summary>Flag settled SDF props as static shadow casters. Only matters for spot/point lights on static GameObjects.
	/// The development scene also makes its spots static while this is on, so the bench can A/B it.</summary>
	[ConVar( "mimiclay_sdf_static_shadows" )]
	public static bool Enabled { get; set; } = false;

	/// <summary>Seconds a prop's shadow inputs must stay unchanged before it joins the cache. Longer = fewer re-caches
	/// around something that moves in bursts; shorter = the saving starts sooner after an edit.</summary>
	[ConVar( "mimiclay_sdf_static_shadows_settle" )]
	public static float SettleSeconds { get; set; } = 2f;

	/// <summary>Minimum seconds between promotion windows (see the class summary).</summary>
	const float PromoteInterval = 0.5f;

	/// <summary>A spot/point light is invalidated by setting its SceneLight transform this far off and back.</summary>
	const float NudgeDistance = 0.01f;

	readonly List<(Vector3 Center, float Radius)> _dirty = new();
	RealTimeSince _sincePromote = 1000f;
	bool _promotedThisFrame;

	/// <summary>True when renderers may join the cache this frame. Demotions never wait.</summary>
	internal bool PromoteWindowOpen { get; private set; }

	// Diagnostics for mimiclay_sdf_static_shadows_stats (all scenes together).
	static int _promotions, _demotions, _flushes, _nudges;
	static RealTimeSince _sinceStats;
	/// <summary>SDF props in the active scene currently held in the light caches.</summary>
	public static int StaticCasters => Game.ActiveScene?.GetAllComponents<SdfRaymarchRenderer>().Count( r => r.StaticShadowCaster ) ?? 0;

	public SdfStaticShadows( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 0, OpenWindow, "SdfStaticShadows.Open" );
		Listen( Stage.FinishUpdate, 10000, Flush, "SdfStaticShadows.Flush" );
	}

	internal static SdfStaticShadows For( Scene scene ) => scene.IsValid() ? scene.GetSystem<SdfStaticShadows>() : null;

	void OpenWindow() => PromoteWindowOpen = _sincePromote >= PromoteInterval;

	/// <summary>A caster joined the cache at these bounds: the lights reaching it must re-cache to include it.</summary>
	internal void Promoted( Vector3 center, float radius )
	{
		_promotions++;
		_promotedThisFrame = true;
		_dirty.Add( (center, radius) );
	}

	/// <summary>A caster left the cache (changed, hidden, destroyed): the lights reaching where it was cached must re-cache.</summary>
	internal void Demoted( Vector3 center, float radius )
	{
		_demotions++;
		_dirty.Add( (center, radius) );
	}

	void Flush()
	{
		if ( _promotedThisFrame )
		{
			_promotedThisFrame = false;
			_sincePromote = 0f;
			PromoteWindowOpen = false;
		}

		if ( _dirty.Count == 0 )
			return;

		var world = Scene.SceneWorld;
		if ( world.IsValid() )
		{
			_flushes++;
			foreach ( var so in world.SceneObjects )
			{
				// Spot and point only: the sun has no static cache. Disabled or shadowless lights are nudged too,
				// since the engine keeps their entry (and its cache) for 2 s after they stop rendering.
				if ( so is not SceneLight light || light is not (SceneSpotLight or ScenePointLight) )
					continue;

				var pos = light.Position;
				float reach = light.Radius;
				foreach ( var (center, radius) in _dirty )
				{
					if ( (center - pos).Length > reach + radius )
						continue;
					Nudge( light );
					break;
				}
			}
		}
		_dirty.Clear();
	}

	static void Nudge( SceneLight light )
	{
		var tx = light.Transform;
		light.Transform = tx.WithPosition( tx.Position + Vector3.Up * NudgeDistance );
		light.Transform = tx;
		_nudges++;
	}

	[ConCmd( "mimiclay_sdf_static_shadows_stats" )]
	public static void Stats()
	{
		float secs = MathF.Max( _sinceStats, 0.001f );
		Log.Info( $"[sdf-static-shadows] enabled={Enabled} static casters now={StaticCasters} | last {secs:0.0}s: "
			+ $"promotions {_promotions}, demotions {_demotions}, flush frames {_flushes}, light re-caches {_nudges} ({_nudges / secs:0.00}/s)" );
		_promotions = _demotions = _flushes = _nudges = 0;
		_sinceStats = 0f;
	}
}
