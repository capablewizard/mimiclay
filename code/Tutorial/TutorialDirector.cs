using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// The guided-tutorial step machine, alive only while the tutorial character's edit session runs. Created by
/// <see cref="TutorialNpc.BeginTutorial"/> on the same NotSaved rig as the session, so it can never outlive it;
/// <see cref="TutorialCard"/> (spawned once per scene, the EnsureHud pattern) binds to <see cref="Current"/>
/// and renders whatever this exposes.
///
/// Steps are POLLED, not evented: each step snapshots what it needs on entry and watches session/target state
/// for the delta (the codebase's per-frame-assertion style; there is no "which property changed" signal to
/// subscribe to). Per-hint results LATCH — a scrub that happened stays counted even if the value scrubs back.
///
/// While a guided step runs, the EditHud is trimmed with its existing per-scene Show* panel flags (the
/// MenuCustomise precedent — flipped on the LIVE component) plus its hint-strip gate; every authored value is
/// captured at start and restored on EVERY exit path — OnDisabled for normal teardown and
/// <see cref="SweepPlayEnd"/> for editor Stop, where an interrupted teardown would otherwise bake the trimmed
/// flags into lobby.scene (exactly how the menu once shipped broken — see MenuCustomise.OnStart).
/// </summary>
[Title( "Tutorial Director" )]
[Category( "Mimiclay" )]
[Icon( "school" )]
public sealed class TutorialDirector : Component
{
	/// <summary>The live director (null = no tutorial running on this machine). The card binds to this.</summary>
	public static TutorialDirector Current { get; private set; }

	/// <summary>The guided session this run belongs to. Wired by TutorialNpc before enable.</summary>
	public SculptEditSession Session { get; set; }

	/// <summary>Still driving a live edit session? The card renders nothing once this drops — the rig's
	/// deferred destroy means the component can outlive the session by a frame.</summary>
	public bool Live => Session.IsValid() && Session.IsEditing;

	public enum Phase
	{
		Step,   // a guided step is up, waiting on its actions
		Praise, // the step just completed — a short "nice!" beat before the next
		Free,   // guidance over (finished or skipped): full editor, "make it yours"
	}

	public Phase State { get; private set; } = Phase.Step;

	/// <summary>Current guided step index (== <see cref="StepCount"/> once guidance is over).</summary>
	public int StepIndex { get; private set; }

	public int StepCount => _steps.Count;

	/// <summary>Hide the EditHud's own hint-strip while guidance runs — the card is doing the teaching, and
	/// the full key list underneath it would drown each step's one instruction.</summary>
	public static bool HintStripHidden
		=> Current.IsValid() && Current.Live && Current.State != Phase.Free;

	// ── What the card renders ────────────────────────────────────────────────────────────────────────────

	public string CardTitle => State == Phase.Free ? "Make it yours" : CurrentStep?.Title ?? "";

	public string CardBody => State == Phase.Free
		? "Play around with everything — press Finish (or Q) when you're done."
		: CurrentStep?.Body;

	public string PraiseText => _praise[Math.Min( StepIndex, _praise.Length - 1 )];

	public IReadOnlyList<Hint> CardHints => State == Phase.Step
		? CurrentStep?.Hints ?? (IReadOnlyList<Hint>)Array.Empty<Hint>()
		: Array.Empty<Hint>();

	/// <summary>Folds everything the card draws, so its BuildHash re-renders exactly when this changes.</summary>
	public int CardSignal
	{
		get
		{
			var hc = new HashCode();
			hc.Add( Live );
			hc.Add( (int)State );
			hc.Add( StepIndex );
			foreach ( var h in CardHints )
				hc.Add( h.Achieved );
			return hc.ToHashCode();
		}
	}

	/// <summary>Skip the rest of the guidance (the card's button). Processed next update — never torn into
	/// from inside a UI click handler.</summary>
	public void RequestSkip() => _skipRequested = true;

	/// <summary>Leave the tutorial entirely (the Free card's button) — routes through the session's
	/// dialog-aware exit, same as Q.</summary>
	public void RequestFinish() => _finishRequested = true;

	bool _skipRequested;
	bool _finishRequested;

	// ── Steps ────────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>One row on the card: a key glyph, its label, and whether the player has done it (latched).</summary>
	public sealed class Hint
	{
		public string Key { get; init; }
		public string Label { get; init; }
		internal Func<TutorialDirector, bool> Check { get; init; }
		public bool Achieved { get; internal set; }
	}

	sealed class Step
	{
		public string Title { get; init; }
		public string Body { get; init; }
		public Hint[] Hints { get; init; } = Array.Empty<Hint>();
		public Action<TutorialDirector> Enter { get; init; }
		// Extra completion gate beyond "every hint achieved" (null = hints alone decide).
		public Func<TutorialDirector, bool> Done { get; init; }
	}

	Step CurrentStep => StepIndex < _steps.Count ? _steps[StepIndex] : null;

	// Per-run instances — hint latches are state, so the list can never be static.
	readonly List<Step> _steps = new();

	static readonly string[] _praise = { "Nice!", "Lovely!", "Got it!", "Beautiful!", "Perfect!" };

	const float PraiseTime = 1.1f;
	RealTimeUntil _praiseOver;

	// Camera-step travel, accumulated per gesture while AltNav drags (Delta is per-frame).
	float _orbitTravel, _dollyTravel, _panTravel;

	// Transform/paint-step baseline: authored brushes by index at step entry (damage + stamp ghost skipped).
	readonly Dictionary<int, BrushSnap> _snap = new();
	int _countAtEnter;

	readonly record struct BrushSnap( Vector3 Position, Vector3 Size, Rotation Rotation,
		Color Color, float Metallic, float Roughness );

	// The scene EditHud (always exists here — the session's EnsureHud ran) and the authored panel flags,
	// captured once so every exit puts back exactly what the scene shipped.
	EditHud _hud;
	bool _flagsCaptured;
	(bool Tools, bool Layers, bool Palette, bool Picker, bool Sliders) _authored;

	EditHud Hud => _hud.IsValid() ? _hud : (_hud = Scene.GetAllComponents<EditHud>().FirstOrDefault());

	protected override void OnStart()
	{
		BuildSteps();
		CaptureHudFlags();

		EnsureCard();
		Current = this;

		EnterStep( 0 );
	}

	protected override void OnDisabled()
	{
		RestoreHudFlags();
		if ( Current == this )
			Current = null;
	}

	/// <summary>Play is ending — put the HUD flags back however teardown fell out (see the class doc).
	/// Called from <see cref="TutorialNpc.SweepPlayEnd"/>.</summary>
	internal static void SweepPlayEnd()
	{
		if ( Current.IsValid() )
			Current.RestoreHudFlags();
		Current = null;
	}

	protected override void OnUpdate()
	{
		if ( !Live )
			return; // the npc is about to tear the rig down; nothing to advance

		if ( _finishRequested )
		{
			_finishRequested = false;
			Session.RequestExit();
			return;
		}

		if ( _skipRequested )
		{
			_skipRequested = false;
			if ( State != Phase.Free )
				EnterFree();
			return;
		}

		TrackCameraTravel();

		switch ( State )
		{
			case Phase.Step:
				var step = CurrentStep;
				if ( step is null )
				{
					EnterFree();
					break;
				}

				// Latch hints first, then judge completion off the latches + the step's own gate.
				foreach ( var h in step.Hints )
				{
					if ( !h.Achieved && h.Check is not null && SafeCheck( h.Check ) )
						h.Achieved = true;
				}

				bool hintsDone = step.Hints.All( h => h.Check is null || h.Achieved );
				if ( hintsDone && (step.Done is null || SafeCheck( step.Done )) )
				{
					State = Phase.Praise;
					_praiseOver = PraiseTime;
				}
				break;

			case Phase.Praise:
				if ( _praiseOver )
				{
					if ( StepIndex + 1 < _steps.Count )
						EnterStep( StepIndex + 1 );
					else
						EnterFree();
				}
				break;

			case Phase.Free:
				break;
		}
	}

	// A throwing predicate must never kill the whole tutorial loop — log once per offender and treat as false.
	readonly HashSet<Func<TutorialDirector, bool>> _warned = new();
	bool SafeCheck( Func<TutorialDirector, bool> f )
	{
		try
		{
			return f( this );
		}
		catch ( Exception e )
		{
			if ( _warned.Add( f ) )
				Log.Warning( $"TutorialDirector: step predicate threw — {e.Message}" );
			return false;
		}
	}

	void EnterStep( int index )
	{
		StepIndex = index;
		State = Phase.Step;
		_steps[index].Enter?.Invoke( this );
	}

	void EnterFree()
	{
		StepIndex = _steps.Count;
		State = Phase.Free;
		RestoreHudFlags(); // the full editor, exactly as the scene authored it
	}

	void TrackCameraTravel()
	{
		if ( !AltNav.Dragging )
			return;

		float d = AltNav.Delta.Length;
		switch ( AltNav.Current )
		{
			case AltNav.Gesture.Orbit: _orbitTravel += d; break;
			case AltNav.Gesture.Dolly: _dollyTravel += d; break;
			case AltNav.Gesture.Pan: _panTravel += d; break;
		}
	}

	// ── The curriculum (M2 skeleton — camera through add-a-shape; blend/undo/symmetry land with M4) ──────

	void BuildSteps()
	{
		_steps.Add( new Step
		{
			Title = "Look around",
			Body = "Get a feel for the camera first.",
			Hints = new[]
			{
				new Hint { Key = "LMB drag", Label = "spin around him", Check = d => d._orbitTravel > 60f },
				new Hint { Key = "RMB drag", Label = "zoom in and out", Check = d => d._dollyTravel > 40f },
				new Hint { Key = "MMB drag", Label = "slide the view", Check = d => d._panTravel > 40f },
			},
			Enter = d => d.ApplyHudFlags( tools: false, layers: false, palette: false, picker: false, sliders: false ),
		} );

		_steps.Add( new Step
		{
			Title = "Pick a piece",
			Body = "He's made of simple shapes, blended together.",
			Hints = new[]
			{
				new Hint { Key = "LMB", Label = "click a shape to select it", Check = d => d.Session.HasSelection },
			},
		} );

		_steps.Add( new Step
		{
			Title = "Push it around",
			Body = "Drag the gizmo to move it, or hold a key and move the mouse.",
			Hints = new[]
			{
				new Hint { Key = "Drag / W", Label = "move it", Check = d => d.AnyBrush( ( b, s ) => (b.Position - s.Position).Length > 1f ) },
				new Hint { Key = "R", Label = "scale it", Check = d => d.AnyBrush( ( b, s ) => (b.Size - s.Size).Length > 0.5f ) },
				new Hint { Key = "E", Label = "spin it", Check = d => d.AnyBrush( ( b, s ) => RotationDelta( b.Rotation, s.Rotation ) > 0.03f ) },
			},
			Enter = d => d.SnapshotBrushes(),
		} );

		_steps.Add( new Step
		{
			Title = "Paint it",
			Body = "Give the selected shape a new colour.",
			Hints = new[]
			{
				new Hint
				{
					Key = "Palette", Label = "pick a colour",
					Check = d => d.AnyBrush( ( b, s ) =>
						ColorDelta( b.Color, s.Color ) > 0.01f
						|| MathF.Abs( b.Metallic - s.Metallic ) > 0.01f
						|| MathF.Abs( b.Roughness - s.Roughness ) > 0.01f ),
				},
			},
			Enter = d =>
			{
				d.SnapshotBrushes();
				d.ApplyHudFlags( tools: false, layers: false, palette: true, picker: true, sliders: false );
			},
		} );

		_steps.Add( new Step
		{
			Title = "Add a shape",
			Body = "Stamp a new piece onto him — a hat, a nose, anything.",
			Hints = new[]
			{
				new Hint { Key = "Space", Label = "open the shapes", Check = d => d.Session.Tool == SculptTool.Sculpt },
				new Hint { Key = "LMB", Label = "stamp it on", Check = d => d.AuthoredCount() > d._countAtEnter },
			},
			Enter = d =>
			{
				d._countAtEnter = d.AuthoredCount();
				d.ApplyHudFlags( tools: true, layers: false, palette: true, picker: true, sliders: false );
			},
		} );
	}

	// ── Step-detection helpers ───────────────────────────────────────────────────────────────────────────

	// Any live authored brush changed against its entry snapshot, index-matched. Reordering shifts indices
	// and could over-trigger — acceptable: reordering IS an edit the player chose to make.
	bool AnyBrush( Func<SdfBrush, BrushSnap, bool> changed )
	{
		var target = Session.Target;
		if ( !target.IsValid() || target.Brushes is null )
			return false;

		foreach ( var (i, snap) in _snap )
		{
			if ( i >= target.Brushes.Count )
				continue;

			var b = target.Brushes[i];
			if ( b.Damage )
				continue;

			if ( changed( b, snap ) )
				return true;
		}
		return false;
	}

	void SnapshotBrushes()
	{
		_snap.Clear();

		var target = Session.Target;
		if ( !target.IsValid() || target.Brushes is null )
			return;

		var ghost = SculptEditSession.PendingStamp( target );
		int authored = Math.Min( target.AuthoredBrushCount, target.Brushes.Count );
		for ( int i = 0; i < authored; i++ )
		{
			var b = target.Brushes[i];
			if ( b.Damage || b == ghost )
				continue;

			_snap[i] = new BrushSnap( b.Position, b.Size, b.Rotation, b.Color, b.Metallic, b.Roughness );
		}
	}

	// Authored brush count with the pending stamp ghost excluded — the ghost is a REAL brush in the list
	// until it's committed or cancelled, and counting it would complete the add step on hover.
	int AuthoredCount()
	{
		var target = Session.Target;
		if ( !target.IsValid() || target.Brushes is null )
			return 0;

		int n = Math.Min( target.AuthoredBrushCount, target.Brushes.Count );
		if ( SculptEditSession.PendingStamp( target ) is not null )
			n--;
		return n;
	}

	// Orientation distance without trusting any angle API: how far the frame's axes moved (0 = identical).
	static float RotationDelta( Rotation a, Rotation b )
		=> (a.Forward - b.Forward).Length + (a.Up - b.Up).Length;

	static float ColorDelta( Color a, Color b )
		=> MathF.Abs( a.r - b.r ) + MathF.Abs( a.g - b.g ) + MathF.Abs( a.b - b.b );

	// ── EditHud trim (coarse, via the existing per-scene panel flags; the finer per-section gate is M3) ──

	void CaptureHudFlags()
	{
		var hud = Hud;
		if ( !hud.IsValid() || _flagsCaptured )
			return;

		_authored = (hud.ShowTools, hud.ShowLayers, hud.ShowPalette, hud.ShowPicker, hud.ShowSliders);
		_flagsCaptured = true;
	}

	void ApplyHudFlags( bool tools, bool layers, bool palette, bool picker, bool sliders )
	{
		var hud = Hud;
		if ( !hud.IsValid() )
			return;

		hud.ShowTools = tools;
		hud.ShowLayers = layers;
		hud.ShowPalette = palette;
		hud.ShowPicker = picker;
		hud.ShowSliders = sliders;
	}

	void RestoreHudFlags()
	{
		var hud = Hud;
		if ( !hud.IsValid() || !_flagsCaptured )
			return;

		hud.ShowTools = _authored.Tools;
		hud.ShowLayers = _authored.Layers;
		hud.ShowPalette = _authored.Palette;
		hud.ShowPicker = _authored.Picker;
		hud.ShowSliders = _authored.Sliders;
	}

	// One instruction card per scene, spawned on first use and kept — the EnsureHud pattern. A PERSISTENT
	// panel, deliberately: a card torn down while hovered would latch HasHovered + the press statics (the
	// panel-deletion trap), so the card outlives every tutorial run and just renders nothing in between.
	void EnsureCard()
	{
		if ( Scene is null || Scene.GetAllComponents<TutorialCard>().Any() )
			return;

		var go = new GameObject( true, "Tutorial HUD" );
		go.Flags |= GameObjectFlags.NotSaved; // runtime-only: never let this end up serialised into an asset
		var panel = go.Components.Create<ScreenPanel>();
		panel.ZIndex = 150; // above the EditHud (100), below the lobby's Round Setup HUD (500)
		go.Components.Create<TutorialCard>();
	}
}
