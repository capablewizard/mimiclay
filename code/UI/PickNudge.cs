using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Replays a one-shot "you changed this" spring on a UI element each time it's poked — the setup menu's banner
/// nudge, map hop, stepper value wobble and Yes/No pill wobble. The engine restarts a CSS animation only when its
/// KEYFRAMES change (Styles.ApplyAnimation), so each motion is defined twice (-a / -b) and every poke flips which one
/// the element gets.
///
/// The class only lives for <see cref="Window"/> after a poke. An element that's re-mounted later (switching game or
/// mode swaps whole option rows) would otherwise start life carrying its old class and spring again on mount.
///
/// Markup: class="step-value @Nudge.Class( nameof( AddHunters ) )"; handler: Nudge.Change( nameof( AddHunters ), dir,
/// () => S.HunterCount, () => ... ) — springs only if the value actually moved. Fold <see cref="Version"/> into BuildHash.
/// </summary>
public sealed class PickNudge
{
	/// <summary>How long a poked class stays on — longer than any of the springs it drives.</summary>
	const float Window = 0.6f;

	readonly Dictionary<string, (int Count, int Dir, RealTimeSince Since)> _pokes = new();

	/// <summary>Changes on every poke/clear — fold into BuildHash.</summary>
	public int Version { get; private set; }

	/// <summary>Poke <paramref name="key"/>. <paramref name="dir"/> picks the -left / -right variant; 0 = no direction.</summary>
	public void Poke( string key, int dir = 0 )
	{
		_pokes.TryGetValue( key, out var p );
		_pokes[key] = (p.Count + 1, dir, 0);
		Version++;
	}

	/// <summary>Run <paramref name="change"/>, and poke only if <paramref name="read"/> differs afterwards — a press
	/// that hits a clamp (or re-picks the current option) doesn't spring.</summary>
	public void Change<T>( string key, int dir, Func<T> read, Action change )
	{
		var was = read();
		change();
		if ( !EqualityComparer<T>.Default.Equals( was, read() ) )
			Poke( key, dir );
	}

	/// <summary>Forget every poke (e.g. on open).</summary>
	public void Clear()
	{
		_pokes.Clear();
		Version++;
	}

	/// <summary>"nudge-right-a" style class for <paramref name="key"/>, or "" if it hasn't been poked in the last
	/// <see cref="Window"/> seconds.</summary>
	public string Class( string key, string prefix = "nudge" )
	{
		if ( !_pokes.TryGetValue( key, out var p ) || p.Since > Window ) return "";
		var side = p.Dir < 0 ? "-left" : p.Dir > 0 ? "-right" : "";
		return $"{prefix}{side}-{(p.Count % 2 == 0 ? "a" : "b")}";
	}
}
