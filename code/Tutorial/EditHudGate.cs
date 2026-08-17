using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>The gateable sections of the sculpt editor's HUD. Compile-checked on purpose — a string key
/// with a typo would silently gate nothing.</summary>
public enum HudSection
{
	Layers,    // left panel: the shape/layer stack
	Tools,     // right panel container
	AddChip,   // the Add Shape / Done chip (and its Space hotkey)
	UndoRedo,  // the undo/redo chip row (and Ctrl+Z / Ctrl+Y)
	EditChips, // Duplicate / Delete chips (and the Delete key)
	Symmetry,  // the X/Y/Z mirror chips
	Palette,   // floating swatch grid
	Picker,    // floating colour wheel + metal/rough column
	Sliders,   // floating blend/round stack incl. the Add/Carve op chip (and the A key)
	ShapeDock, // bottom-centre shape tiles (and the 1-7 hotkeys)
	WorldSelect, // clicking clay in the world to select it, and the hover wireframe ghost that trails it

	// The in-world gizmo's handle families (RuntimeBrushGizmo). Locked = drawn ghosted (very low alpha)
	// and inert — the tutorial's "this one next" treatment while it teaches one transform at a time.
	GizmoMove,   // axis arrows, plane squares, screen-move disc
	GizmoRotate, // rings, trackball, screen ring
	GizmoScale,  // axis dots, uniform-scale dot
}

public enum SectionState
{
	Hidden,    // not rendered at all
	Locked,    // rendered dimmed; clicks and hotkeys swallowed ("not yet")
	Normal,    // behaves exactly as outside the tutorial
	Highlight, // Normal + a pulsing ring: "this one, here"
}

/// <summary>
/// The tutorial's per-section HUD gate. <see cref="EditHud"/> consults it at every gated section and
/// <see cref="SculptEditSession"/> at every section-owned hotkey; while <see cref="Active"/> is false —
/// everywhere outside a guided tutorial step — every query answers Normal and the HUD is byte-for-byte
/// unchanged. Pure runtime static, NEVER serialized, so unlike the EditHud's Show* flags there is no
/// authored value to restore and nothing an interrupted teardown can bake into a scene — ending a run is
/// just <see cref="End"/>, and <see cref="TutorialDirector"/> calls it on every exit path (statics survive
/// the editor's Stop→Play, so the play-end sweep calls it too).
///
/// Interaction rules the consumers implement (the trap each avoids):
/// <list type="bullet">
/// <item>A Locked PANEL keeps its pointer events and mounts a <c>tutorial-lock-shield</c> overlay that eats
/// clicks — never pointer-events:none on the panel, which would drop world clicks through it (deselecting,
/// stamping) the moment the cursor crossed it.</item>
/// <item>A Locked CHIP inside a live panel gets <c>tutorial-locked</c> (pointer-events:none + dim): its
/// clicks land harmlessly on the panel behind it, which keeps the pointer-over-UI protection intact.</item>
/// <item>Hotkeys check <see cref="Interactive"/> for the section that owns them, so the keyboard can't do
/// what the hidden/locked button can't.</item>
/// </list>
/// </summary>
public static class EditHudGate
{
	/// <summary>A tutorial step is gating the HUD. False = every section reports Normal.</summary>
	public static bool Active { get; private set; }

	/// <summary>Bumped on every change — folded into <see cref="EditHud"/>'s BuildHash (missed hashes
	/// fail silently, so the version is the one thing the HUD needs to track).</summary>
	public static int Version { get; private set; }

	static readonly Dictionary<HudSection, SectionState> _states = new();

	public static SectionState Of( HudSection section )
		=> Active && _states.TryGetValue( section, out var s ) ? s : SectionState.Normal;

	/// <summary>Should the section render at all?</summary>
	public static bool Visible( HudSection section ) => Of( section ) != SectionState.Hidden;

	/// <summary>May the section's clicks and hotkeys act?</summary>
	public static bool Interactive( HudSection section )
		=> Of( section ) is SectionState.Normal or SectionState.Highlight;

	/// <summary>Extra classes for a PANEL-level section (dim rides the panel's own opacity where it has an
	/// inline one — see the palette/picker/slider floats, whose inline style would override a class).</summary>
	public static string PanelClass( HudSection section ) => Of( section ) switch
	{
		SectionState.Locked => "tutorial-dim",
		SectionState.Highlight => "tutorial-highlight",
		_ => "",
	};

	/// <summary>Extra classes for a CHIP inside a live panel (locked chips go pointer-events:none — safe
	/// there, the panel behind them still catches the click).</summary>
	public static string ChipClass( HudSection section ) => Of( section ) switch
	{
		SectionState.Locked => "tutorial-locked",
		SectionState.Highlight => "tutorial-highlight",
		_ => "",
	};

	/// <summary>Basic shapes only (sphere / box / cylinder / cone): trims the advanced tiles — extruded
	/// profile, spline, text — from the shape dock AND their 5-7 hotkeys. The tutorial's first shape lesson
	/// sets it; cleared by <see cref="Begin"/>/<see cref="End"/> so it can never outlive its step.</summary>
	public static bool BasicShapesOnly { get; private set; }

	public static void SetBasicShapesOnly( bool on )
	{
		if ( !Active || BasicShapesOnly == on )
			return;

		BasicShapesOnly = on;
		Version++;
	}

	/// <summary>Start gating: every section drops to <paramref name="baseline"/> (a guided step then raises
	/// the few it teaches). Callers follow with <see cref="Set"/> per exception.</summary>
	public static void Begin( SectionState baseline = SectionState.Hidden )
	{
		Active = true;
		_states.Clear();
		BasicShapesOnly = false;
		foreach ( var s in Enum.GetValues<HudSection>() )
			_states[s] = baseline;
		Version++;
	}

	public static void Set( HudSection section, SectionState state )
	{
		if ( !Active )
			return;

		// Idempotent, so per-frame assertions (a step's Tick driving a section off live state) don't bump
		// the version — and so re-render the whole HUD — every frame.
		if ( _states.TryGetValue( section, out var current ) && current == state )
			return;

		_states[section] = state;
		Version++;
	}

	/// <summary>Stop gating — the HUD is instantly back to its authored self. Safe to call redundantly.</summary>
	public static void End()
	{
		Active = false;
		_states.Clear();
		BasicShapesOnly = false;
		Version++;
	}
}
