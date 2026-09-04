using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// The Charades word pool — sourced from <see cref="CharadesWordList"/> assets via ResourceLibrary (create
/// and edit lists in the editor, not in code) — plus the couple of string rules everyone has to agree on:
/// how a word is masked for the guessers' hint, and how a guess is normalised before comparing (the host
/// validates guesses, but the HUD may someday want a local "you're close" pre-check, so the rules live here
/// rather than in the manager). The built-in lists live under Assets/Charades/*.words.
/// </summary>
public static class CharadesWords
{
	/// <summary>Every word the given topic selection allows: the union of every non-hidden
	/// <see cref="CharadesWordList"/> asset whose topic is selected, de-duplicated by normalised form
	/// (two lists both containing "duck" is one "duck"). An empty/None selection reads as Everything —
	/// a game can never start with zero topics to draw from.</summary>
	public static List<string> PoolFor( CharadesTopics topics )
	{
		if ( (topics & CharadesTopics.Everything) == CharadesTopics.None )
			topics = CharadesTopics.Everything;

		var pool = new List<string>();
		var seen = new HashSet<string>();

		foreach ( var list in ResourceLibrary.GetAll<CharadesWordList>() )
		{
			if ( list is null || list.Hidden || (list.Topic & topics) == CharadesTopics.None )
				continue;

			foreach ( var word in list.Words ?? Enumerable.Empty<string>() )
			{
				var norm = Normalize( word );
				if ( norm.Length == 0 || !seen.Add( norm ) )
					continue;

				pool.Add( word.Trim() );
			}
		}

		// No assets at all (they were deleted, or a broken mount) — better a one-word game that visibly
		// says why than a manager stalled forever in Choosing with nothing to offer.
		if ( pool.Count == 0 )
		{
			Log.Warning( "CharadesWords: no Charades Word List assets found — create some (Assets/Charades/*.words)." );
			pool.Add( "clay" );
		}

		return pool;
	}

	/// <summary>Draw <paramref name="count"/> distinct words from the selected topics, avoiding anything in
	/// <paramref name="used"/> until the pool runs dry (then <paramref name="used"/> is cleared and everything
	/// is fair game again — a long session repeating beats a session that stalls).</summary>
	public static List<string> Draw( int count, CharadesTopics topics, HashSet<string> used )
	{
		var pool = PoolFor( topics );

		var fresh = pool.Where( w => !used.Contains( w ) ).ToList();
		if ( fresh.Count < count )
		{
			used.Clear();
			fresh = pool;
		}

		var drawn = fresh.OrderBy( _ => Random.Shared.Next() ).Take( count ).ToList();

		// A tiny pool (assets missing, or a one-word custom list) still has to fill the mimic's three
		// offer slots — repeat rather than hand BeginTurn a short list it will index past.
		while ( drawn.Count > 0 && drawn.Count < count )
			drawn.Add( drawn[0] );

		return drawn;
	}

	/// <summary>The guessers' hint: every letter masked, spaces kept — "ice cream" → "___ _____". The HUD
	/// letter-spaces it for display; this is the synced form.</summary>
	public static string Mask( string word )
		=> new( word.Select( c => c == ' ' ? ' ' : '_' ).ToArray() );

	/// <summary>Canonical form for guess comparison: lower-cased, punctuation stripped, whitespace folded —
	/// so "Hot-Dog!" matches "hot dog", and a bot mimic's sculpt-save name like "duck (2)" still matches a
	/// typed "duck 2". Letters, digits and single spaces are all that survive.</summary>
	public static string Normalize( string text )
	{
		var kept = new System.Text.StringBuilder( (text ?? "").Length );
		foreach ( var c in (text ?? "").ToLowerInvariant() )
		{
			if ( char.IsLetterOrDigit( c ) )
				kept.Append( c );
			else if ( char.IsWhiteSpace( c ) || c == '-' || c == '_' )
				kept.Append( ' ' ); // separators count as word gaps, never as letters
		}

		return string.Join( ' ', kept.ToString()
			.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) );
	}
}
