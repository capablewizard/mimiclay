using System;
using System.Collections.Generic;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// The props' taunt sounds — the ONE list the taunt pie (TauntPie) draws and the taunt RPC indexes into
/// (HiderController.BroadcastTaunt). Add a taunt by adding a line: the pie splits into as many equal slices as
/// there are entries. Entry 0 is the whistle — the tap-T taunt and the Hunt's automatic one.
/// </summary>
public static class Taunts
{
	public readonly record struct Taunt( string Emoji, string Sound );

	// Order = pie order, clockwise from 12 o'clock: the whistle on top, then the rest shortest → longest (the wav's
	// length in the comment — keep a new taunt in its place by length).
	//
	// A getter that builds the array, NOT a static readonly field: hotload carries static field VALUES across from the
	// old assembly, so an edited field initialiser never re-runs mid-session (reordering the pie did nothing until
	// a restart). Rebuilt on each read — eight small records, read a handful of times a frame while the pie is up.
	public static IReadOnlyList<Taunt> All => new Taunt[]
	{
		new( "😗", "sounds/game/tauntwhistyle.sound" ),        // 0.9 s
		new( "🦆", "sounds/game/taunts/duckquack.sound" ),     // 1.0 s
		new( "🤡", "sounds/game/taunts/clownhorn.sound" ),     // 1.1 s
		new( "🙌", "sounds/game/taunts/applause.sound" ),      // 6.9 s
		new( "👏", "sounds/game/taunts/handclaps.sound" ),     // 8.1 s
		new( "🎺", "sounds/game/taunts/trumpet.sound" ),       // 8.6 s
		new( "😴", "sounds/game/taunts/snoring.sound" ),       // 12.0 s
		new( "🐔", "sounds/game/taunts/chicken.sound" ),       // 18.5 s
	};

	/// <summary>Global gain on every taunt, on top of each .sound's own Volume (those are balanced against each other —
	/// tune loudness here, not per sound).</summary>
	public const float Volume = 4f;

	/// <summary>Seconds a still-playing taunt takes to fade out when the same source starts a new one.</summary>
	public const float CutFade = 0.3f;

	/// <summary>Play taunt <paramref name="index"/> from <paramref name="source"/> on THIS machine, following it as it
	/// moves (so a long one — snoring, applause — keeps coming from it). One taunt per source at a time:
	/// <paramref name="playing"/> is that source's current one, faded out quickly (no click) and replaced.</summary>
	public static void Play( GameObject source, Vector3 position, int index, ref SoundHandle playing )
	{
		if ( index < 0 || index >= All.Count || !source.IsValid() )
			return;

		if ( playing.IsValid() )
			playing.Stop( CutFade );

		var handle = Sound.Play( All[index].Sound, position );
		playing = handle;
		if ( !handle.IsValid() )
			return;
		handle.Parent = source;
		handle.FollowParent = true;
		handle.Volume *= Volume;
	}

	/// <summary>Is <paramref name="handle"/> still sounding (and not on its way out from a cut)?</summary>
	public static bool IsPlaying( SoundHandle handle ) => handle.IsValid() && handle.IsPlaying;

	/// <summary>The whistle: the tap taunt and the automatic Hunt taunt.</summary>
	public const int Whistle = 0;

	/// <summary>The slice centred on <paramref name="angle"/> — screen-space radians, 0 = straight up, clockwise.
	/// Slice 0 sits at the top.</summary>
	public static int SliceAt( float angle )
	{
		int n = All.Count;
		float slice = MathF.Tau / n;
		float a = (angle + slice * 0.5f) % MathF.Tau;
		if ( a < 0f ) a += MathF.Tau;
		return Math.Clamp( (int)(a / slice), 0, n - 1 );
	}

	/// <summary>The centre angle of slice <paramref name="index"/> (same convention as <see cref="SliceAt"/>).</summary>
	public static float SliceAngle( int index ) => MathF.Tau / All.Count * index;
}
