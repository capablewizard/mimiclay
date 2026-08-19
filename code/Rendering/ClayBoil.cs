using System;

namespace Mimiclay;

/// <summary>
/// Claymation "boil" for ONE prop — the stop-motion wobble that stops its plasticine surface sitting
/// perfectly still. Add this alongside an <see cref="SdfRaymarchRenderer"/> on the same GameObject;
/// props without one don't boil at all.
///
/// Deliberately opt-in per prop rather than scene-wide: a whole room boiling at once reads as the
/// picture being unstable rather than as the props being handmade, so this is for the few things
/// meant to draw the eye.
///
/// The mechanism: each tick, the displacement noise (and optionally the surface maps) are sampled at
/// a slightly different offset, held for the whole tick, then jumped. It rides displacement — on a
/// renderer with Displace OFF, <see cref="ForceDisplace"/> (default on) borrows it for the boil's
/// duration, so the lumps appear while the clay is handled and settle away with the boil.
/// <see cref="TextureJitter"/> works regardless either way, since it shifts the triplanar maps
/// rather than the field.
///
/// Disabling the component turns the boil off (the lookup is enabled-only), so it doubles as a toggle.
///
/// WHEN a prop boils is <see cref="Activation"/>: characters boil <see cref="BoilActivation.Always"/> —
/// the animator's hands are on them every frame because they move. A static prop isn't being handled and
/// shouldn't churn... until it's shot: <see cref="BoilActivation.WhileDamaged"/> keeps the boil inert
/// until the sibling sculpture is remodelled — a healing prop churns from impact through the heal and
/// settles the moment the clay is whole again (the animator smooths it over and takes their hands off);
/// a scarring prop gets a brief <see cref="ImpactTicks"/> shudder as each crater lands, then stillness.
/// While a boil is ACTIVE, the heal animation itself snaps to the boil's tick (see SdfShrinkSystem):
/// remodelled clay moves in poses, and the healing crater is part of what's being remodelled.
/// </summary>
[Title( "Clay Boil" )]
[Category( "SDF" )]
[Icon( "blur_on" )]
public sealed class ClayBoil : Component
{
	/// <summary>Ticks per second — how often the surface re-forms. 12 would be "on twos" at a 24fps
	/// film rate; the default is deliberately slower than that, which reads as a hand-made budget
	/// stop-motion and leaves each pose on screen long enough to be seen as a pose. Past ~16 it stops
	/// reading as animation and starts reading as shimmer. 0 disables the boil entirely.
	///
	/// This is also the prop's field re-BAKE rate: each tick re-dispatches the GPU field eval with the
	/// new offsets (the lumps live in the baked field, not in the march), so the boil costs a few
	/// compute dispatches per second — the same work a brush edit already does — instead of per-pixel
	/// noise every frame.</summary>
	[Property, Range( 0f, 30f ), Group( "Boil" )] public float Fps { get; set; } = 4f;

	/// <summary>How far the noise jumps each tick, in noise CELLS (so it's independent of the
	/// material's DispFreq — retuning lump density won't retune the boil).
	///
	/// Sizing this by eye is a trap — work back from pixels. Surface movement per tick is roughly
	/// 2·DispAmp·(0.28·Jitter), so at the stock plasticine (amp 1.357) Jitter 0.1 buys ~0.08" of
	/// travel, which is about ONE pixel at normal viewing distance: mathematically present, visually
	/// nothing. ~0.35 gives 4–5px, which is where it starts to read as animation.</summary>
	[Property, Range( 0f, 1f ), Group( "Boil" )] public float Jitter { get; set; } = 0.4f;

	/// <summary>Per-tick lump-depth wobble, as a fraction of the material's DispAmp — the surface
	/// reading deeper/shallower frame to frame.
	///
	/// Reads STRONGER per unit than <see cref="Jitter"/>: it scales every point of the surface the
	/// same way, so the movement adds up coherently instead of partly cancelling the way a directional
	/// noise offset does. The default is the top of the range (+/-50% depth per tick), which lands
	/// well because the slow <see cref="Fps"/> gives each depth time to register as a pose.
	///
	/// Cost: the march's step-safety bound is sized from the WORST tick (amp × (1 + AmpJitter/2)),
	/// so at 1.0 this prop understeps at 1.5× the factor it would otherwise need. Since the lumps
	/// are baked into the field that is the whole cost — the noise itself is paid at bake, not per
	/// march step.</summary>
	[Property, Range( 0f, 1f ), Group( "Boil" )] public float AmpJitter { get; set; } = 1f;

	/// <summary>If the renderer's Displace is OFF, turn displacement ON for the duration of the boil.
	/// The churn IS displacement movement, so a smooth prop would otherwise only boil via
	/// <see cref="TextureJitter"/> — near-invisible at the default. The borrowed lumps use the
	/// renderer's DispAmp/DispFreq (those dials stay meaningful with Displace off — tune the boil's
	/// lump look there), land with the boil's first tick and vanish the frame it settles: the clay
	/// roughens while the animator's hands are on it and is smoothed flat when they let go. No effect
	/// on a renderer that already displaces. Turn OFF to author a maps-only boil on smooth clay.</summary>
	[Property, Group( "Boil" )] public bool ForceDisplace { get; set; } = true;

	/// <summary>When this prop's clay counts as "being handled" (see the class doc for the fiction).
	/// <see cref="BoilActivation.Always"/> for things that move — characters, held guns.
	/// <see cref="BoilActivation.WhileDamaged"/> for static props: inert until the clay is remodelled —
	/// churning while any shrinking crater exists (grace period included — the wound churns before it
	/// heals), plus an instant impact jolt whenever the brush list changes (one full tick, then
	/// <see cref="ImpactTicks"/> of follow-through on the global clock), which is what makes a
	/// SCARRING prop (no DamageProfile — craters never shrink) shudder the frame it's shot instead
	/// of the crater popping in on perfectly still clay. <see cref="BoilActivation.Never"/> is the
	/// kill switch: no boil ever, but the tuned dials stay on the prop.</summary>
	[Property, Group( "Boil" )] public BoilActivation Activation { get; set; } = BoilActivation.Always;

	/// <summary>Snap the heal animation (shrinking craters — see SdfShrinkSystem) to the boil tick
	/// while the boil is running. On by default: remodelled clay moves in poses, and a healing crater
	/// gliding smoothly across a surface that only moves in ticks reads as a mismatch. Turn OFF to let
	/// craters heal smoothly per-frame regardless of the boil — total heal time is identical either
	/// way, this is purely how the animation is sampled.</summary>
	[Property, Group( "Boil" )] public bool LockHeal { get; set; } = true;

	/// <summary>WhileDamaged only: how many ADDITIONAL boil ticks play after the impact pose, on the
	/// GLOBAL boil clock. The impact pose itself is not this dial's to give — any brush-list change
	/// (a carve landing, a healed crater's final removal, a mid-round reshape) instantly splices in
	/// one full tick of boil, so the clay always jolts the frame it's hit; this is the follow-through.
	/// 0 = jolt, hold one tick, settle. 2 = jolt, then two more re-forms on the global rhythm before
	/// stillness. Tell-safe: decoys and player disguises receive carves identically on every machine,
	/// so both shudder identically.</summary>
	[Property, Range( 0f, 8f ), Group( "Boil" )] public float ImpactTicks { get; set; } = 1f;

	/// <summary>Set (only) by the runtime presentation systems that CREATE boils on scene clay (creative's
	/// hover/heal churn — see RoundOutlineSystem.ApplySceneBoils): marks the component as system-owned, so
	/// its <see cref="Activation"/> is ASSERTED both ways every frame and the component is destroyed once
	/// dormant. Never author this — a mapper's hand-placed boil keeps it false and its Activation is
	/// treated as the authored look. A durable flag on the COMPONENT rather than bookkeeping in a system:
	/// a hotload rebuilds GameObjectSystems with empty memory, and a runtime boil orphaned mid-hover that
	/// way had its transient Always latched in forever (the creative stuck-boiling-props bug).</summary>
	[Property, Hide] public bool RuntimeManaged { get; set; }

	/// <summary>The boil pose (unseeded, wrapped tick) at a given time — THE clock. Consumed by the
	/// renderer's field bake, the shader's live tick (pushed as the BoilTick attribute) and the heal's
	/// tick-lock, so all three agree by construction. Normally the GLOBAL tick (floor of time × Fps —
	/// real stop-motion advances every model on the same frame; only the seed varies per prop); for
	/// the first 1/Fps seconds of an impact burst it's the burst's own off-grid pose, so the jolt
	/// lands the frame the shot does instead of at the next global boundary.</summary>
	public float TickAt( float time )
	{
		if ( Activation == BoilActivation.WhileDamaged
			&& time >= _burstStart && time < _burstUntil
			&& time < _burstStart + 1f / MathF.Max( Fps, 0.001f ) )
			return _impactPose;

		// Wrapped to 1024 ticks: hash13's frac() runs out of float32 mantissa on an unwrapped tick in
		// a long session — the boil would quietly slow down and then freeze.
		return MathF.Floor( time * Fps ) % 1024f;
	}

	/// <summary>Is the boil currently running? (Named to dodge the engine's Component.Active.) Consulted
	/// every frame by the renderer (attributes + bake tick), the highlight's step-safety bound and the
	/// heal's tick-lock, so all four flip together the frame activation changes — a dormant boil is
	/// indistinguishable from no component at all.</summary>
	public bool Boiling
	{
		get
		{
			if ( Activation == BoilActivation.Always )
				return true;
			if ( Activation == BoilActivation.Never )
				return false;

			// WhileDamaged: an impact burst is running, or any shrinking crater on the sibling
			// sculpture = the clay is being remodelled.
			if ( Time.Now < _burstUntil )
				return true;

			var sculpt = GameObject.Components.Get<SdfSculpture>();
			if ( sculpt?.Brushes is not { Count: > 0 } brushes )
				return false;
			foreach ( var b in brushes )
				if ( b.Shrinks )
					return true;
			return false;
		}
	}

	// Impact-burst state: brush-COUNT watcher, not a full hash — the burst is about material arriving
	// or leaving (carve lands, crater vanishes), and count changes exactly then. A hash would also
	// burst on every animation frame of a heal, which the Shrinks check already covers.
	int _lastBrushCount = -1;
	float _burstUntil = -1f;
	float _burstStart;
	float _impactPose;

	/// <summary>Watch the sibling sculpture for remodels. Deliberately in OnUpdate rather than inside
	/// <see cref="Boiling"/>: the getter is hit several times a frame from different systems, and a
	/// getter that mutates burst state would make the answer depend on who asked first. Cross-component
	/// update order being unordered just means the burst can start one frame late — invisible at boil
	/// rates. The first observation only RECORDS the count (no burst): a network snapshot delivers the
	/// whole brush list at spawn, and a freshly spawned prop shouldn't arrive shuddering.</summary>
	protected override void OnUpdate()
	{
		// No ImpactTicks gate: the instant impact pose is unconditional in WhileDamaged mode — the
		// dial only sets how much follow-through plays after it.
		if ( Activation != BoilActivation.WhileDamaged || Fps <= 0f )
			return;

		int count = GameObject.Components.Get<SdfSculpture>()?.Brushes?.Count ?? -1;
		if ( count >= 0 && _lastBrushCount >= 0 && count != _lastBrushCount )
		{
			// Re-stamped on a mid-burst change too — rapid fire re-jolts the clay on every impact.
			_burstStart = Time.Now;

			// The impact pose: off the integer grid (+0.5) so no global tick can ever produce it —
			// the jolt is visible even if the prop was already mid-boil from a heal — and derived
			// from the hit time so back-to-back shots each roll a fresh pose.
			_impactPose = MathF.Floor( Time.Now * 997f ) % 1024f + 0.5f;

			// Active through the full impact pose (one tick from the hit), then to the ImpactTicks'th
			// global boundary after the hit's period — the follow-through re-forms ride the global
			// rhythm. Max because with ImpactTicks 0 the boundary term lands before the pose ends.
			float g = MathF.Floor( Time.Now * Fps );
			_burstUntil = MathF.Max( Time.Now + 1f / Fps, (g + 1f + ImpactTicks) / Fps );
		}
		_lastBrushCount = count;
	}

	/// <summary>Per-tick shift of the triplanar surface maps, in texture REPEATS — the one that makes
	/// the fingerprints and fine imperfections change rather than the silhouette. Works even with the
	/// renderer's Displace off, since it shifts the maps rather than the field.
	///
	/// Two regimes. Well under 1 repeat the texture SLIDES: detail keeps its identity and crawls.
	/// Around/above 1 it lands on uncorrelated texels and genuinely RE-FORMS, which is what really
	/// happens between stop-motion frames — but full re-rolls fizz and give TAA nothing to track. The
	/// default sits FAR into the slide regime (a fiftieth of a repeat): in practice the fine detail
	/// wants only the faintest crawl. Treat 0.25+ as a stylistic choice, not a starting point.
	///
	/// Unlike the other dials this is free — applied once at the hit point during shading, not inside
	/// the march loop, so it costs no steps.</summary>
	[Property, Range( 0f, 2f ), Group( "Boil" )] public float TextureJitter { get; set; } = 0.02f;

	/// <summary>Write this prop's boil onto the raymarcher's scene-object attributes. These feed the
	/// live texture jitter and the analytic fallback; the displacement boil itself is baked into the
	/// distance field per tick (the renderer folds the tick into its field hash).</summary>
	public void Apply( RenderAttributes a )
	{
		a.Set( "BoilFps", MathF.Max( Fps, 0f ) );
		a.Set( "BoilJitter", MathF.Max( Jitter, 0f ) );
		a.Set( "BoilAmpJitter", MathF.Max( AmpJitter, 0f ) );
		a.Set( "BoilTexJitter", MathF.Max( TextureJitter, 0f ) );
		a.Set( "BoilTick", TickAt( Time.Now ) );
	}

	/// <summary>Dump why every live boil is (or isn't) running, plus any sculpture still holding a Shrinks
	/// brush — the "this prop won't stop churning" diagnostic. One line per component: activation, the
	/// Boiling verdict and which leg produced it (burst window remaining vs live shrink brushes, each with
	/// its age against delay+duration), and enough context (pawn above? proxy? NotSaved?) to tell a scene
	/// decoy from a released pawn prop from a thumbnail rig at a glance.</summary>
	[ConCmd( "mimi_boil_debug" )]
	public static void DebugDump()
	{
		var scene = Game.ActiveScene;
		if ( !scene.IsValid() )
			return;

		foreach ( var boil in scene.GetAllComponents<ClayBoil>() )
		{
			float burst = MathF.Max( 0f, boil._burstUntil - Time.Now );
			Log.Info( $"[boil] {Describe( boil.GameObject )}: {boil.Activation}{(boil.RuntimeManaged ? " (runtime)" : "")} " +
				$"Fps={boil.Fps} Boiling={boil.Boiling} burst={burst:0.00}s lastCount={boil._lastBrushCount}" );
			DumpShrinks( boil.GameObject.Components.Get<SdfSculpture>() );
		}

		// Shrinks with NO enabled boil beside them — a prop that churns in this state means stale render
		// attributes, not a live component (see ApplyOff).
		foreach ( var sculpt in scene.GetAllComponents<SdfSculpture>() )
		{
			if ( sculpt.GameObject.Components.Get<ClayBoil>().IsValid() )
				continue;
			if ( sculpt.Brushes is not { Count: > 0 } brushes || !brushes.Exists( b => b.Shrinks ) )
				continue;
			Log.Info( $"[boil] {Describe( sculpt.GameObject )}: NO ClayBoil but shrink brushes remain:" );
			DumpShrinks( sculpt );
		}
	}

	static void DumpShrinks( SdfSculpture sculpt )
	{
		if ( sculpt?.Brushes is not { Count: > 0 } brushes )
			return;
		for ( int i = 0; i < brushes.Count; i++ )
		{
			var b = brushes[i];
			if ( b.Shrinks )
				Log.Info( $"   [{i}] shrink age={b.ShrinkAge:0.00} / delay={b.ShrinkDelay:0.00}+dur={b.ShrinkDuration:0.00} size={b.Size.x:0.0} dmg={b.Damage}" );
		}
	}

	static string Describe( GameObject go )
	{
		string pawn = go.Components.Get<HiderController>( FindMode.EverythingInSelfAndAncestors ).IsValid() ? " hider"
			: go.Components.Get<HunterController>( FindMode.EverythingInSelfAndAncestors ).IsValid() ? " hunter" : "";
		string flags = go.Flags.HasFlag( GameObjectFlags.NotSaved ) ? " notsaved" : "";
		string net = go.Network.Active ? (go.IsProxy ? " proxy" : " owned") : "";
		return $"'{go.Name}'{pawn}{net}{flags}";
	}

	/// <summary>Write "no boil" onto a scene object's attributes. Must be called for props WITHOUT a
	/// ClayBoil, not just skipped: attributes persist on the scene object, so a prop that had the
	/// component removed (or disabled) at runtime would otherwise keep boiling on the last values it
	/// was given. Zeroing Fps alone would do it — the shader short-circuits on it — but the rest are
	/// zeroed too so a stale value can't leak into the march's step-safety factor.</summary>
	public static void ApplyOff( RenderAttributes a )
	{
		a.Set( "BoilFps", 0f );
		a.Set( "BoilJitter", 0f );
		a.Set( "BoilAmpJitter", 0f );
		a.Set( "BoilTexJitter", 0f );
		a.Set( "BoilTick", 0f );
	}
}

/// <summary>See <see cref="ClayBoil.Activation"/>.</summary>
public enum BoilActivation
{
	/// <summary>Boil whenever the component is enabled — for clay that's always being handled (characters).</summary>
	Always,

	/// <summary>Boil only while the sibling sculpture is being remodelled — a shrinking crater exists,
	/// or its brush list just changed (see <see cref="ClayBoil.ImpactTicks"/>). For static props that
	/// are only "in the animator's hands" from the moment they're shot until the clay settles.</summary>
	WhileDamaged,

	/// <summary>Never boil — behaves exactly like having no ClayBoil at all, but keeps the component
	/// and its tuned dials wired so the look can be switched back on without re-authoring them.</summary>
	Never,
}
