using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// One layer of the camera's resting depth-of-field look. Drop it anywhere in a scene — a map scene for a
/// per-map look, the system scene for a per-game default — and <see cref="MainCamera"/> folds it over the
/// camera's own authored look every frame. Only the values you set override; a blank (null) value falls
/// through to the layer below.
///
/// Layering, lowest first:
/// 1. <see cref="MainCamera"/>'s Authored* properties (the camera prefab — the global default).
/// 2. Every enabled DofProfile whose <see cref="Modes"/> matches the running game, in <see cref="Priority"/>
///    order (ties: a mode-filtered profile beats an any-mode one).
/// 3. Runtime drivers (the sculpt edit session's focus pull, ...) writing through <see cref="MainCamera.Dof"/>
///    per frame. They take over completely while asserting — even over a profile that turns DoF off — and the
///    camera eases back to the resolved 1+2 look once they stop.
///
/// Profiles are registered statically but only count for the scene the camera lives in, so editor tabs and
/// thumbnail scenes never leak into play.
///
/// Examples: "no DoF in creative anywhere" = a system-scene profile with Modes [Creative], Enabled off.
/// "No DoF in creative on FlatGrass only" = the same profile placed in FlatGrass.scene instead. "FlatGrass
/// focuses further out in every game" = a FlatGrass profile with no modes and just Focal Distance set.
///
/// Everything is live: edit a profile (or enable/disable it) during play and the camera eases to the new look.
/// </summary>
[Title( "DoF Profile" )]
[Category( "Mimiclay" )]
[Icon( "center_focus_weak" )]
public sealed class DofProfile : Component
{
	/// <summary>Live profiles, in no particular order. Registered while enabled, so <see cref="MainCamera"/>
	/// can resolve per frame without scanning the scene.</summary>
	internal static readonly List<DofProfile> All = new();

	/// <summary>Games this profile applies to. Empty = every game, and also outside a game (menu, lobby).
	/// Non-empty = only while one of these games runs.</summary>
	[Property] public List<GameModeKind> Modes { get; set; } = new();

	/// <summary>Higher layers on top. Ties go to the mode-filtered profile, so a map-wide look and a
	/// "this map in creative" tweak in the same scene stack without touching priorities.</summary>
	[Property] public int Priority { get; set; }

	/// <summary>Turn the resting DoF on or off. Off eases the blur to zero (the engine skips the effect
	/// entirely at zero blur, so it costs nothing).</summary>
	[Property, Group( "Overrides" ), Title( "Enabled" )] public bool? DofEnabled { get; set; }

	/// <summary>Blur strength. Engine units: relative to a 1080p screen height, so it reads the same at any
	/// resolution.</summary>
	[Property, Group( "Overrides" ), Range( 0f, 100f )] public float? BlurSize { get; set; }

	/// <summary>Focal distance in world units.</summary>
	[Property, Group( "Overrides" ), Range( 1f, 16000f )] public float? FocalDistance { get; set; }

	/// <summary>Sharp band around the focal distance, in world units.</summary>
	[Property, Group( "Overrides" ), Range( 1f, 15000f )] public float? FocusRange { get; set; }

	internal bool Matches( GameModeKind? game ) =>
		Modes is null || Modes.Count == 0 || (game is { } g && Modes.Contains( g ));

	internal bool ModeFiltered => Modes is { Count: > 0 };

	internal static int Compare( DofProfile a, DofProfile b )
	{
		int p = a.Priority.CompareTo( b.Priority );
		return p != 0 ? p : a.ModeFiltered.CompareTo( b.ModeFiltered );
	}

	protected override void OnEnabled()
	{
		if ( !All.Contains( this ) )
			All.Add( this );
	}

	protected override void OnDisabled()
	{
		All.Remove( this );
	}
}
