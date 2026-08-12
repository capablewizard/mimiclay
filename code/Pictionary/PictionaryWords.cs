using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// The Pictionary word pool + the couple of string rules everyone has to agree on: how a word is masked for
/// the guessers' hint, and how a guess is normalised before comparing (the host validates guesses, but the
/// HUD may someday want to pre-check "you're close" locally, so the rules live here rather than in the manager).
///
/// The pool is deliberately things you can plausibly SCULPT in soft clay in ~2 minutes: chunky silhouettes,
/// no abstract nouns. Kept in code for the block-out; promote to an asset when curation starts mattering.
/// </summary>
public static class PictionaryWords
{
	static readonly string[] Pool =
	{
		// Animals — big readable silhouettes.
		"snake", "duck", "elephant", "giraffe", "snail", "octopus", "penguin", "turtle",
		"shark", "whale", "spider", "butterfly", "crab", "frog", "mouse", "cat",
		"dog", "rabbit", "owl", "flamingo", "hedgehog", "seahorse", "dinosaur", "worm",

		// Food — classic clay fare.
		"banana", "pizza", "mushroom", "carrot", "ice cream", "donut", "cupcake", "pretzel",
		"hot dog", "cheese", "watermelon", "pineapple", "egg", "croissant",

		// Objects — around-the-house shapes.
		"chair", "teapot", "umbrella", "guitar", "rocket", "boat", "hammer", "glasses",
		"crown", "candle", "balloon", "book", "clock", "ladder", "anchor", "bell",
		"key", "scissors", "toothbrush", "trophy", "wheelbarrow", "telescope",

		// Outdoors & bigger things.
		"tree", "cactus", "snowman", "volcano", "lighthouse", "windmill", "bridge", "igloo",
		"castle", "tent", "rainbow", "cloud", "moon", "mountain",

		// People-ish & silly.
		"wizard", "robot", "ghost", "mermaid", "pirate", "angel", "skeleton", "chef",
	};

	/// <summary>Draw <paramref name="count"/> distinct words, avoiding anything in <paramref name="used"/>
	/// until the pool runs dry (then <paramref name="used"/> is cleared and everything is fair game again —
	/// a long session repeating beats a session that stalls).</summary>
	public static List<string> Draw( int count, HashSet<string> used )
	{
		var fresh = Pool.Where( w => !used.Contains( w ) ).ToList();
		if ( fresh.Count < count )
		{
			used.Clear();
			fresh = Pool.ToList();
		}

		return fresh.OrderBy( _ => Random.Shared.Next() ).Take( count ).ToList();
	}

	/// <summary>The guessers' hint: every letter masked, spaces kept — "ice cream" → "___ _____". The HUD
	/// letter-spaces it for display; this is the synced form.</summary>
	public static string Mask( string word )
		=> new( word.Select( c => c == ' ' ? ' ' : '_' ).ToArray() );

	/// <summary>Case/whitespace-insensitive canonical form for guess comparison.</summary>
	public static string Normalize( string text )
		=> string.Join( ' ', (text ?? "").Trim().ToLowerInvariant()
			.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) );
}
