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
/// While a guided step runs, the EditHud is shaped through <see cref="EditHudGate"/> — per-section
/// Hidden/Locked/Normal/Highlight, plus the hint-strip gate here. The gate is a pure runtime static (nothing
/// authored, nothing serialized), so ending a run is just <see cref="EditHudGate.End"/> — called from
/// OnDisabled for normal teardown and <see cref="SweepPlayEnd"/> for editor Stop (statics survive Stop→Play,
/// the MenuCustomise-bake class of bug this design retires).
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

	/// <summary>The character's speech bubble — the tutorial's friendly voice, driven through the bubble's
	/// runtime TextOverride (never its serialized Text). Wired by TutorialNpc; null-safe throughout, so a
	/// bubble-less character just skips straight to each step's card.</summary>
	public SpeechBubble Bubble { get; set; }

	/// <summary>Still driving a live edit session? The card renders nothing once this drops — the rig's
	/// deferred destroy means the component can outlive the session by a frame.</summary>
	public bool Live => Session.IsValid() && Session.IsEditing;

	/// <summary>Should the instruction card render? The two voices take turns: while the character is
	/// still typing a step's bubble line (<see cref="Phase.Speak"/>), the deadpan card holds back — and
	/// the Free sign-off is voice-only (no window at all; Q is the exit, as the monologue says).</summary>
	public bool CardVisible => Live && State is Phase.Step or Phase.Praise;

	public enum Phase
	{
		Speak,  // the character is delivering the step's bubble line — the card waits
		Step,   // a guided step is up, waiting on its actions
		Praise, // the step just completed — a short "nice!" beat before the next
		Free,   // guidance over (finished or skipped): full editor, "make it yours"
	}

	public Phase State { get; private set; } = Phase.Step;

	/// <summary>Current guided step index (== <see cref="StepCount"/> once guidance is over).</summary>
	public int StepIndex { get; private set; }

	public int StepCount => _steps.Count;

	/// <summary>A tutorial is live on this machine, any phase (free-play included). Screen furniture that
	/// would fight the card or make no sense mid-tutorial — the lobby's "Press G" status line — gates on
	/// this.</summary>
	public static bool IsRunning => Current.IsValid() && Current.Live;

	/// <summary>Hide the EditHud's own hint-strip while guidance runs — the card is doing the teaching, and
	/// the full key list underneath it would drown each step's one instruction.</summary>
	public static bool HintStripHidden => IsRunning && Current.State != Phase.Free;

	// ── What the card renders ────────────────────────────────────────────────────────────────────────────

	// (No Free branches: the card never renders in the Free phase — the sign-off is voice-only.)
	public string CardTitle => CurrentStep?.Title ?? "";

	public string CardBody => CurrentStep?.Body;

	string PraiseText => _praise[Math.Min( StepIndex, _praise.Length - 1 )];

	// Praise included: the completed card holds its ticked hints through the beat while HE says the
	// praise line in his bubble.
	public IReadOnlyList<Hint> CardHints => State is Phase.Step or Phase.Praise
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


	// ── Steps ────────────────────────────────────────────────────────────────────────────────────────────

	/// <summary>One row on the card: an optional input-prompt icon, the action label, and whether the
	/// player has done it (latched). No key-chip text — an action isn't a button (that framing was tried
	/// and cut); where no icon fits, the label alone carries the row.</summary>
	public sealed class Hint
	{
		/// <summary>Input-prompt art (path under Assets, e.g. "inputicons/mouse_left.png") — jittering on
		/// the clay clock until achieved. For MOUSE prompts; keyboard prompts use <see cref="KeyCap"/>.</summary>
		public string Icon { get; init; }

		/// <summary>Keyboard prompt drawn as the possess-toast's key cap (cream chip, header-font letter)
		/// — same jitter-until-achieved as the icons. Null + null Icon = label-only row.</summary>
		public string KeyCap { get; init; }

		public string Label { get; init; }
		internal Func<TutorialDirector, bool> Check { get; init; }
		public bool Achieved { get; internal set; }
	}

	sealed class Step
	{
		public string Title { get; init; }
		public string Body { get; init; }
		// The character's spoken intro for the step (typed into his speech bubble; the card waits for it).
		// Null = no line, the card appears immediately.
		public string Bubble { get; init; }
		public Hint[] Hints { get; init; } = Array.Empty<Hint>();
		// Runs at EnterStep (so the HUD gate applies while he's still talking) AND again when the card
		// appears — snapshots and counters re-baseline then, so nothing done during the speech pre-ticks.
		public Action<TutorialDirector> Enter { get; init; }
		// Runs every frame while the step's card is up — for reactive dialogue (Say) and mid-step state.
		public Action<TutorialDirector> Tick { get; init; }
		// Extra completion gate beyond "every hint achieved" (null = hints alone decide).
		public Func<TutorialDirector, bool> Done { get; init; }
	}

	Step CurrentStep => StepIndex < _steps.Count ? _steps[StepIndex] : null;

	// Per-run instances — hint latches are state, so the list can never be static.
	readonly List<Step> _steps = new();

	// One per step, in step order (clamps on the last) — spoken by HIM in the bubble on completion.
	// INSTANCE, not static, deliberately: hotload preserves static field VALUES, so copy stored in a
	// static array kept its old text across recompiles until an editor restart. A fresh director is made
	// every run, so instance copy hotloads naturally. Same rule for every tutorial string below.
	readonly string[] _praise =
	{
		"Nice!", "Lovely!", "Got it!", "Smooth!", "Beautiful!", "You're a natural!",
		"Slick!", "Whoosh!", "Ooh, fancy!", "A transformation!", "Gorgeous!", "Masterful!", "An artist!", "Perfection!",
	};

	const float PraiseTime = 1.1f;
	RealTimeUntil _praiseOver;

	// The beat between the bubble line landing its last letter and the card appearing — a breath, so the
	// window doesn't slam in on the final blip.
	const float SpeakBeat = 0.35f;
	bool _typedSeen;
	RealTimeSince _sinceTyped;

	// Camera-step travel, accumulated per gesture while AltNav drags (Delta is per-frame).
	float _orbitTravel, _dollyTravel, _panTravel;

	// Scroll-push notches, accumulated while the wheel can actually push (something selected, session
	// live) — the depth lesson requires these AND a real position delta.
	float _scrollTravel;

	// ── Chained bubble monologues ────────────────────────────────────────────────────────────────────────
	// One shared chain (there's only ever one monologue at a time): line N+1 begins ChainBeat after line N
	// lands its LAST TYPED LETTER. Reset at every step entry; a step's Tick (or the Free phase) drives it.
	// Copy lives in instance arrays — the hotload rule, see _praise.
	const float ChainBeat = 1.5f;
	int _chainStage;
	bool _chainArmed;
	RealTimeSince _chainTyped;
	bool _chainDone; // latched by a step that WAITS on its chain — completing the task mustn't cut him off

	readonly string[] _addPlacingLines =
	{
		"Click the shapes below to change the shape you're adding, or use the number keys.",
		"All the hotkeys we used before work in this mode too!",
		"E rotates, R scales and mousewheel moves forwards and backwards.",
	};

	readonly string[] _optionsLines =
	{
		"Shapes can have different qualities.",
		"You can make them add, carve or change how they blend here.",
		"These are hotkeyed to A, S, D and F.",
	};

	readonly string[] _selectLines =
	{
		"I'm just a bunch of simple shapes, all blended together. Try clicking one!",
		"Pressing Tab toggles the shape wireframes.",
	};

	readonly string[] _layerLines =
	{
		"How the shapes are layered can have a big effect on your final sculpt.",
		"Grab a layer to move it up or down the stack.",
	};

	readonly string[] _freeLines =
	{
		"And that's it! You've got full control so have a play with the tools, go wild!",
		"Press Q when you're happy with your creation to return to the game.",
	};

	// Transform/paint-step baseline: authored brushes by index at step entry (damage + stamp ghost skipped).
	readonly Dictionary<int, BrushSnap> _snap = new();
	int _countAtEnter;

	readonly record struct BrushSnap( Vector3 Position, Vector3 Size, Rotation Rotation,
		Color Color, float Metallic, float Roughness,
		SdfOperation Operation, float Blend, float Rounding, float Curvature, float Slice,
		SdfShape Shape );

	// The layer lesson's baseline: the authored brush objects in stack order at step entry. A genuine
	// reorder = the SAME objects in a different sequence; an undo swaps the objects wholesale (the undo
	// state stores copies) and deliberately doesn't count.
	List<SdfBrush> _orderSnap;

	protected override void OnStart()
	{
		BuildSteps();

		EnsureCard();
		Current = this;

		EnterStep( 0 );
	}

	protected override void OnDisabled()
	{
		EditHudGate.End();
		if ( Bubble.IsValid() )
			Bubble.TextOverride = null; // his bubble goes back to the authored invite
		if ( Current == this )
			Current = null;
	}

	/// <summary>Play is ending — drop the HUD gate however teardown fell out (see the class doc).
	/// Called from <see cref="TutorialNpc.SweepPlayEnd"/>.</summary>
	internal static void SweepPlayEnd()
	{
		EditHudGate.End();
		if ( Current.IsValid() && Current.Bubble.IsValid() )
			Current.Bubble.TextOverride = null;
		Current = null;
	}

	protected override void OnUpdate()
	{
		if ( !Live )
			return; // the npc is about to tear the rig down; nothing to advance

		TrackCameraTravel();

		switch ( State )
		{
			case Phase.Speak:
			{
				// Wait for the bubble line to finish typing (plus a breath), then bring the card up. The
				// step's Enter re-runs at that moment so snapshots/counters baseline against what the
				// player did DURING the speech — an orbit while he talks must not pre-tick "look around".
				bool typed = !Bubble.IsValid() || Bubble.FullyTyped;
				if ( typed && !_typedSeen )
				{
					_typedSeen = true;
					_sinceTyped = 0f;
				}
				else if ( !typed )
				{
					_typedSeen = false; // retyping (text changed under us) — wait again
				}

				if ( _typedSeen && _sinceTyped > SpeakBeat )
				{
					CurrentStep?.Enter?.Invoke( this );
					State = Phase.Step;
				}
				break;
			}

			case Phase.Step:
				var step = CurrentStep;
				if ( step is null )
				{
					EnterFree();
					break;
				}

				// Reactive dialogue first — a Say here retypes the bubble without re-gating the card.
				step.Tick?.Invoke( this );

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
					SetBubble( PraiseText ); // HE does the praising — the card just holds its ticked hints
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
				// The sign-off monologue — full control is already live; he just talks you out the door.
				SpeakChain( _freeLines );
				break;
		}
	}

	// ── The shared chain drive ───────────────────────────────────────────────────────────────────────────

	void ResetChain()
	{
		_chainStage = 0;
		_chainArmed = false;
		_chainDone = false;
	}

	// Speak the chain's current line, stepping to the next a beat after each fully types. Returns true once
	// the FINAL line has fully typed (the "he's finished talking" signal steps can wait on).
	bool SpeakChain( string[] lines )
	{
		Say( lines[Math.Min( _chainStage, lines.Length - 1 )] );

		bool typed = !Bubble.IsValid() || Bubble.FullyTyped;
		if ( !typed )
		{
			_chainArmed = false;
			return false;
		}

		if ( _chainStage >= lines.Length - 1 )
			return true;

		if ( !_chainArmed )
		{
			_chainArmed = true;
			_chainTyped = 0f;
		}
		if ( _chainTyped > ChainBeat )
		{
			_chainStage++;
			_chainArmed = false;
		}
		return false;
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
		var step = _steps[index];

		ResetChain();

		// Gate (and first baseline) immediately — the trimmed HUD is right for the speech too; the card
		// waits on the bubble line when the step has one.
		step.Enter?.Invoke( this );
		SetBubble( step.Bubble );
		_typedSeen = false;
		State = step.Bubble is null ? Phase.Step : Phase.Speak;
	}

	void EnterFree()
	{
		StepIndex = _steps.Count;
		State = Phase.Free;
		ResetChain(); // the sign-off monologue (_freeLines) runs from the Free case each frame
		EditHudGate.End(); // the full editor, exactly as the scene authored it
	}

	// Speak through the character's bubble — the runtime override channel, never its serialized Text (an
	// in-editor play session would bake the line into the scene). Null step line = say nothing: an empty
	// override pops the bubble out rather than falling back to the authored invite.
	void SetBubble( string line )
	{
		if ( Bubble.IsValid() )
			Bubble.TextOverride = line ?? "";
	}

	/// <summary>Mid-step dialogue: swap what he's saying without touching the card (the bubble retypes on
	/// the change; setting the same line again is a no-op). For step Tick hooks.</summary>
	public void Say( string line ) => SetBubble( line );

	void TrackCameraTravel()
	{
		// Scroll-push notches — counted only when the wheel could genuinely push (selection held, no drag
		// owning the wheel), mirroring the session's own gate loosely.
		if ( Session.HasSelection && !Session.IsManipulating && !AltNav.Dragging )
			_scrollTravel += MathF.Abs( Input.MouseWheel.y );

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
		// Two voices per step: the character's friendly bubble line first, then the deadpan card — header
		// + key hints, no chatter (the chatter is his job).

		_steps.Add( new Step
		{
			Bubble = "Welcome to the tutorial, let's learn how to move the camera first.",
			Title = "Look Around",
			Hints = new[]
			{
				new Hint { Icon = "inputicons/mouse_left.png", Label = "Orbit the camera", Check = d => d._orbitTravel > 60f },
				new Hint { Icon = "inputicons/mouse_scroll.png", Label = "Pan the camera", Check = d => d._panTravel > 40f },
				new Hint { Icon = "inputicons/mouse_right.png", Label = "Zoom in / out", Check = d => d._dollyTravel > 40f },
			},
			Enter = d =>
			{
				d.ApplyGate(); // everything hidden — just him, the camera and the voices
				d.ResetCameraTravel();
			},
		} );

		_steps.Add( new Step
		{
			Bubble = _selectLines[0], // same string as the chain's first line, so the card-appear doesn't retype
			Title = "Pick a Shape",
			Hints = new[]
			{
				new Hint { Icon = "inputicons/mouse_left.png", Label = "Select a shape", Check = d => d.Session.HasSelection },
			},
			// The Tab tip chains in after the intro; a quick click mustn't cut it off.
			Done = d => d._chainDone,
			Tick = d =>
			{
				if ( d.SpeakChain( d._selectLines ) )
					d._chainDone = true;
			},
			// Selection unlocks HERE — the camera stage keeps clicks pure camera (no picking, no hover
			// ghost), so a stray tap can't select a shape before it's been introduced. The gizmo stays
			// fully HIDDEN (the gate baseline) through this step and its praise — its first appearance is
			// the move lesson's entry, landing exactly on "This is the gizmo!".
			Enter = d => d.ApplyGate( (HudSection.WorldSelect, SectionState.Normal) ),
		} );

		// ── Section: the gizmo, one transform at a time. The families not being taught draw ghosted and
		// inert (GizmoMove/Rotate/Scale gate sections → RuntimeBrushGizmo), and the checks require an
		// actual gizmo drag (IsManipulating) so the W/R/E scrubs can't complete a gizmo lesson. ──────────

		_steps.Add( new Step
		{
			Bubble = "This is the gizmo! Drag any of the handles to move the shape.",
			Title = "Use the Gizmo",
			Hints = new[]
			{
				new Hint
				{
					Label = "Drag the handles to move the shape",
					Check = d => d.LockIn( d.AnyBrush( ( b, s ) => (b.Position - s.Position).Length > 1f ), d.Session.IsManipulating ),
				},
			},
			Tick = NeedSelectionTick,
			Enter = d =>
			{
				d.SnapshotBrushes();
				d.ApplyGate(
					(HudSection.WorldSelect, SectionState.Normal),
					(HudSection.GizmoMove, SectionState.Normal),
					(HudSection.GizmoRotate, SectionState.Locked),
					(HudSection.GizmoScale, SectionState.Locked) );
			},
		} );

		_steps.Add( new Step
		{
			Bubble = "Now twist it with the rings.",
			Title = "Use the Gizmo",
			Hints = new[]
			{
				new Hint
				{
					Label = "Drag the rings to spin it",
					Check = d => d.LockIn( d.AnyBrush( ( b, s ) => RotationDelta( b.Rotation, s.Rotation ) > 0.03f ), d.Session.IsManipulating ),
				},
			},
			Tick = NeedSelectionTick,
			Enter = d =>
			{
				d.SnapshotBrushes();
				d.ApplyGate(
					(HudSection.WorldSelect, SectionState.Normal),
					(HudSection.GizmoMove, SectionState.Locked),
					(HudSection.GizmoRotate, SectionState.Normal),
					(HudSection.GizmoScale, SectionState.Locked) );
			},
		} );

		_steps.Add( new Step
		{
			Bubble = "And stretch it with the dots.",
			Title = "Use the Gizmo",
			Hints = new[]
			{
				new Hint
				{
					Label = "Drag the dots to scale it",
					Check = d => d.LockIn( d.AnyBrush( ( b, s ) => (b.Size - s.Size).Length > 0.5f ), d.Session.IsManipulating ),
				},
			},
			Tick = NeedSelectionTick,
			Enter = d =>
			{
				d.SnapshotBrushes();
				d.ApplyGate(
					(HudSection.WorldSelect, SectionState.Normal),
					(HudSection.GizmoMove, SectionState.Locked),
					(HudSection.GizmoRotate, SectionState.Locked),
					(HudSection.GizmoScale, SectionState.Normal) );
			},
		} );

		// ── Section: the same transforms on the keyboard scrubs. The whole gizmo ghosts (all three
		// families Locked) so the keys are the only way through; the checks require the matching scrub
		// to be live, so a lingering gizmo drag can't tick them. ─────────────────────────────────────────

		_steps.Add( new Step
		{
			Bubble = "Pros use the keyboard! Hold W and move the mouse.",
			Title = "Push It Around",
			Hints = new[]
			{
				new Hint
				{
					KeyCap = "W", Label = "Move it",
					Check = d => d.LockIn(
						BrushScrub.Active == ScrubKind.Move && d.AnyBrush( ( b, s ) => (b.Position - s.Position).Length > 1f ),
						d.Session.IsScrubbing ),
				},
			},
			Tick = NeedSelectionTick,
			Enter = d =>
			{
				d.SnapshotBrushes();
				d.ApplyGate(
					(HudSection.WorldSelect, SectionState.Normal),
					(HudSection.GizmoMove, SectionState.Locked),
					(HudSection.GizmoRotate, SectionState.Locked),
					(HudSection.GizmoScale, SectionState.Locked) );
			},
		} );

		_steps.Add( new Step
		{
			Bubble = "Hold E to spin it.",
			Title = "Push It Around",
			Hints = new[]
			{
				new Hint
				{
					KeyCap = "E", Label = "Spin it",
					Check = d => d.LockIn(
						BrushScrub.Active == ScrubKind.Rotate && d.AnyBrush( ( b, s ) => RotationDelta( b.Rotation, s.Rotation ) > 0.03f ),
						d.Session.IsScrubbing ),
				},
			},
			Tick = NeedSelectionTick,
			Enter = d => d.SnapshotBrushes(), // gate carries over from the step before
		} );

		_steps.Add( new Step
		{
			Bubble = "And R to scale it.",
			Title = "Push It Around",
			Hints = new[]
			{
				new Hint
				{
					KeyCap = "R", Label = "Scale it",
					Check = d => d.LockIn(
						BrushScrub.Active == ScrubKind.Scale && d.AnyBrush( ( b, s ) => (b.Size - s.Size).Length > 0.5f ),
						d.Session.IsScrubbing ),
				},
			},
			Tick = NeedSelectionTick,
			Enter = d => d.SnapshotBrushes(),
		} );

		_steps.Add( new Step
		{
			Bubble = "Scroll the mousewheel to push it away, or pull it closer.",
			Title = "Push It Around",
			Hints = new[]
			{
				new Hint
				{
					// Both halves required: notches scrolled AND the shape actually displaced — so a scrub
					// can't tick it, and neither can dead scrolling with the push blocked.
					Icon = "inputicons/mouse_scroll_vertical.png", Label = "Push / pull the shape",
					Check = d => d._scrollTravel >= 2f
						&& d.AnyBrush( ( b, s ) => (b.Position - s.Position).Length > 1f ),
				},
			},
			Tick = NeedSelectionTick,
			Enter = d =>
			{
				d.SnapshotBrushes();
				d._scrollTravel = 0f;
			},
		} );

		_steps.Add( new Step
		{
			Bubble = "How about a fresh coat of colour?",
			Title = "Paint It",
			Hints = new[]
			{
				new Hint
				{
					Label = "Pick a colour",
					Check = d => d.AnyBrush( ( b, s ) =>
						ColorDelta( b.Color, s.Color ) > 0.01f
						|| MathF.Abs( b.Metallic - s.Metallic ) > 0.01f
						|| MathF.Abs( b.Roughness - s.Roughness ) > 0.01f ),
				},
			},
			Enter = d =>
			{
				d.SnapshotBrushes();
				// The palette is the lesson (ringed); the picker sits beside it visible but locked — "there's
				// more here, not yet". Everything else stays gone; selection and the (learned) gizmo stay live.
				d.ApplyGate(
					(HudSection.WorldSelect, SectionState.Normal),
					(HudSection.GizmoMove, SectionState.Normal),
					(HudSection.GizmoRotate, SectionState.Normal),
					(HudSection.GizmoScale, SectionState.Normal),
					(HudSection.Palette, SectionState.Highlight),
					(HudSection.Picker, SectionState.Locked) );
			},
		} );

		_steps.Add( new Step
		{
			Bubble = "Now add something new. A hat? A nose? Surprise me!",
			Title = "Add a Shape",
			Hints = new[]
			{
				new Hint { KeyCap = "Space", Label = "Open the shapes", Check = d => d.Session.Tool == SculptTool.Sculpt },
				new Hint { Icon = "inputicons/mouse_left.png", Label = "Stamp it on", Check = d => d.AuthoredCount() > d._countAtEnter },
			},
			// Reactive: the moment the Add tool is up (chip or Space), the shape dock appears and he runs
			// the placing monologue (shapes line → hotkeys reminder, ChainBeat apart). Backing out
			// (Done/Esc) without stamping hides the dock, resets the chain and returns to the step's own
			// line. (Dock visibility also gates the 1-7 hotkeys, so key and dock agree.)
			Tick = d =>
			{
				bool placing = d.Session.Tool == SculptTool.Sculpt;
				// Highlighted, not just visible — the dock is what the dialogue is pointing at.
				EditHudGate.Set( HudSection.ShapeDock, placing ? SectionState.Highlight : SectionState.Hidden );

				if ( !placing )
				{
					d.ResetChain();
					d.Say( d.CurrentStep.Bubble );
					return;
				}

				d.SpeakChain( d._addPlacingLines );
			},
			Enter = d =>
			{
				d._countAtEnter = d.AuthoredCount();
				// The Add chip is the lesson (ringed) inside a live Tools panel; the tools you haven't met
				// yet sit locked beside it. Paint stays usable — colour the new piece as you place it. The
				// shape dock starts HIDDEN; the Tick above reveals it once the Add tool is up.
				d.ApplyGate(
					(HudSection.WorldSelect, SectionState.Normal),
					(HudSection.GizmoMove, SectionState.Normal),
					(HudSection.GizmoRotate, SectionState.Normal),
					(HudSection.GizmoScale, SectionState.Normal),
					(HudSection.Tools, SectionState.Normal),
					(HudSection.AddChip, SectionState.Highlight),
					(HudSection.UndoRedo, SectionState.Locked),
					(HudSection.EditChips, SectionState.Locked),
					(HudSection.Symmetry, SectionState.Locked),
					(HudSection.Palette, SectionState.Normal),
					(HudSection.Picker, SectionState.Normal) );

				// AFTER ApplyGate (Begin clears the flag): primitives only for the first shape lesson —
				// the extruded/spline/text tiles and their 5-7 hotkeys wait for free play.
				EditHudGate.SetBasicShapesOnly( true );
			},
		} );

		_steps.Add( new Step
		{
			Bubble = "When a shape is selected, you can change its shape or colour with these buttons.",
			Title = "Shape or Colour",
			Hints = new[]
			{
				// Shape is a brush PROPERTY — a dock tile (or 1-4) with a selection CONVERTS it in place;
				// colour covers the palette swatches AND the wheel/metal/rough column.
				new Hint
				{
					Label = "Change its shape or colour",
					Check = d => d.AnyBrush( ( b, s ) => b.Shape != s.Shape
						|| ColorDelta( b.Color, s.Color ) > 0.01f
						|| MathF.Abs( b.Metallic - s.Metallic ) > 0.01f
						|| MathF.Abs( b.Roughness - s.Roughness ) > 0.01f ),
				},
			},
			Tick = NeedSelectionTick,
			Enter = d =>
			{
				// Out of the stamp tool first: the add step can leave Sculpt mode live with a fresh ghost,
				// and dock tiles would re-mould THAT instead of converting a selection — the lesson here.
				d.Session.SetTool( SculptTool.Gizmo );
				d.SnapshotBrushes();
				// The dock AND the whole colour section (palette + wheel column) are the subject, all
				// ringed and persistent (they render whenever a brush is active — the fresh stamp is
				// still selected from the last step). Still primitives only.
				d.ApplyGate(
					(HudSection.WorldSelect, SectionState.Normal),
					(HudSection.GizmoMove, SectionState.Normal),
					(HudSection.GizmoRotate, SectionState.Normal),
					(HudSection.GizmoScale, SectionState.Normal),
					(HudSection.Tools, SectionState.Normal),
					(HudSection.AddChip, SectionState.Locked),
					(HudSection.UndoRedo, SectionState.Locked),
					(HudSection.EditChips, SectionState.Locked),
					(HudSection.Symmetry, SectionState.Locked),
					(HudSection.ShapeDock, SectionState.Highlight),
					(HudSection.Palette, SectionState.Highlight),
					(HudSection.Picker, SectionState.Highlight) );
				EditHudGate.SetBasicShapesOnly( true );
			},
		} );

		// ── The full editor unveiled: a quick lap of what's left before he hands over the keys. ──────────

		_steps.Add( new Step
		{
			Bubble = _optionsLines[0], // same string as the chain's first line, so the card-appear doesn't retype
			Title = "Shape Options",
			Hints = new[]
			{
				new Hint
				{
					Label = "Change any shape option",
					Check = d => d.AnyBrush( ( b, s ) =>
						b.Operation != s.Operation
						|| MathF.Abs( b.Blend - s.Blend ) > 0.05f
						|| MathF.Abs( b.Rounding - s.Rounding ) > 0.02f
						|| MathF.Abs( b.Curvature - s.Curvature ) > 0.02f
						|| MathF.Abs( b.Slice - s.Slice ) > 0.02f ),
				},
			},
			// The step also waits for the hotkey line to land — finishing the task mustn't cut him off.
			Done = d => d._chainDone,
			Tick = d =>
			{
				if ( !d.Session.HasSelection )
				{
					d._chainArmed = false;
					d.Say( "You'll need to pick a piece first!" );
					return;
				}

				if ( d.SpeakChain( d._optionsLines ) )
					d._chainDone = true;
			},
			Enter = d =>
			{
				d.SnapshotBrushes();
				// FULL editor from here on — this lap tours it rather than trimming it. The slider stack
				// (op chip + blend/round/wildcard) is the subject; the layer stack alone stays back, so its
				// own stage gets the reveal.
				EditHudGate.Begin( SectionState.Normal );
				EditHudGate.Set( HudSection.Sliders, SectionState.Highlight );
				EditHudGate.Set( HudSection.Layers, SectionState.Hidden );
			},
		} );

		_steps.Add( new Step
		{
			Bubble = _layerLines[0], // same string as the chain's first line, so the card-appear doesn't retype
			Title = "The Layer Stack",
			Hints = new[]
			{
				new Hint { Icon = "inputicons/mouse_left.png", Label = "Drag a layer up or down", Check = d => d.OrderChanged() },
			},
			// The instruction line chains in after the intro; completing early mustn't cut it off.
			Done = d => d._chainDone,
			Tick = d =>
			{
				if ( d.SpeakChain( d._layerLines ) )
					d._chainDone = true;
			},
			Enter = d =>
			{
				d.SnapshotOrder();
				EditHudGate.Begin( SectionState.Normal );
				EditHudGate.Set( HudSection.Layers, SectionState.Highlight );
			},
		} );
	}

	void ResetCameraTravel()
	{
		_orbitTravel = 0f;
		_dollyTravel = 0f;
		_panTravel = 0f;
	}

	// Shared reactive line for every transform lesson: a right-click deselect leaves nothing to transform
	// — coach the player back, then return to the step's own line once something is selected again.
	// A static METHOD (not a stored delegate): hotload replaces method BODIES but preserves static field
	// values, so a delegate cached in a static field would keep executing its pre-edit copy.
	static void NeedSelectionTick( TutorialDirector d )
		=> d.Say( d.Session.HasSelection ? d.CurrentStep.Bubble : "You'll need to pick a piece first!" );

	// One call per step: baseline everything Hidden, then raise exactly the sections the step teaches.
	void ApplyGate( params (HudSection Section, SectionState State)[] overrides )
	{
		EditHudGate.Begin();
		foreach ( var o in overrides )
			EditHudGate.Set( o.Section, o.State );
	}

	// ── Step-detection helpers ───────────────────────────────────────────────────────────────────────────

	// A transform lesson completes only when the operation is LOCKED IN — the delta must be seen while the
	// gesture is live (that's what attributes it to the right tool), but the hint only ticks once the
	// gesture has been released. Mid-drag praise felt like the tutorial snatching the mouse.
	bool _gesturePending;

	bool LockIn( bool deltaDuringGesture, bool gestureLive )
	{
		if ( gestureLive && deltaDuringGesture )
			_gesturePending = true;
		return _gesturePending && !gestureLive;
	}

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
		// Every transform lesson re-baselines through here (step entry AND card-appear), so the lock-in
		// latch resets with the snapshots it's judged against.
		_gesturePending = false;

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

			_snap[i] = new BrushSnap( b.Position, b.Size, b.Rotation, b.Color, b.Metallic, b.Roughness,
				b.Operation, b.Blend, b.Rounding, b.Curvature, b.Slice, b.Shape );
		}
	}

	// The layer lesson: capture the authored stack order (the brush OBJECTS in sequence), then detect a
	// genuine reorder — the same objects, different sequence. An undo replaces the objects wholesale (the
	// undo state stores copies), so it deliberately reads as "not a reorder".
	void SnapshotOrder()
	{
		var target = Session.Target;
		int authored = target.IsValid() && target.Brushes is not null
			? Math.Min( target.AuthoredBrushCount, target.Brushes.Count ) : 0;
		_orderSnap = target.IsValid()
			? target.Brushes.Take( authored ).ToList()
			: new List<SdfBrush>();
	}

	bool OrderChanged()
	{
		var target = Session.Target;
		if ( _orderSnap is null || !target.IsValid() || target.Brushes is null )
			return false;

		int authored = Math.Min( target.AuthoredBrushCount, target.Brushes.Count );
		if ( authored != _orderSnap.Count )
			return false; // add/delete/undo — not a reorder

		bool moved = false;
		for ( int i = 0; i < authored; i++ )
		{
			var b = target.Brushes[i];
			if ( !_orderSnap.Contains( b ) )
				return false; // an object we never snapshotted — the list was rebuilt (undo), not reordered
			if ( !ReferenceEquals( b, _orderSnap[i] ) )
				moved = true;
		}
		return moved;
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
