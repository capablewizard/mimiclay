using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// The lobby's topic SELECTION — which lists feed the phrase pool — as it travels: a comma-joined string of
/// topic ids, synced inside <see cref="CharadesSettings"/> and couriered as-is. Two id shapes:
/// a built-in topic's id is its display name normalised ("Animals" → "animals"; see <see cref="IdFor"/>),
/// a community list's id is its workshop file id with the <see cref="WorkshopPrefix"/> ("ws:3123456789").
/// The empty selection means "every built-in topic" — the default, and what the Everything chip means.
/// </summary>
public static class CharadesTopics
{
	/// <summary>The default selection: every built-in topic (and no community lists).</summary>
	public const string Everything = "";

	/// <summary>The Standard selection meaning "no built-in topics at all" — only valid while a Community list
	/// is ticked. It's an id no list has, so it matches nothing on its own.</summary>
	public const string NoStandard = "none";

	/// <summary>Everything a Topics game draws from: the Standard selection (empty = every built-in, expanded
	/// here so it survives being joined with community ids) plus the ticked Community lists.</summary>
	public static string Combined( string standard, string community )
	{
		var communityIds = Parse( community );
		if ( communityIds.Count == 0 )
			return standard ?? Everything;

		var ids = Parse( standard );
		if ( ids.Count == 0 )
			foreach ( var t in CharadesWords.BuiltInTopics() )
				ids.Add( t.Id );

		ids.Remove( NoStandard );
		ids.UnionWith( communityIds );
		return Join( ids );
	}

	/// <summary>Prefix marking a community (Steam Workshop) list's id in a selection.</summary>
	public const string WorkshopPrefix = "ws:";

	/// <summary>Split a selection into its ids (empty for <see cref="Everything"/>).</summary>
	public static HashSet<string> Parse( string selection )
	{
		var set = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var part in (selection ?? "").Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
			set.Add( part );
		return set;
	}

	/// <summary>Join ids back into a selection string (sorted, so equal sets compare equal).</summary>
	public static string Join( IEnumerable<string> ids )
		=> string.Join( ',', ids.Where( id => !string.IsNullOrWhiteSpace( id ) ).Distinct().OrderBy( id => id, StringComparer.Ordinal ) );

	/// <summary>A built-in topic's id from its display name: normalised, spaces to dashes ("Sea Life" → "sea-life").</summary>
	public static string IdFor( string topicTitle )
		=> CharadesWords.Normalize( topicTitle ).Replace( ' ', '-' );

	/// <summary>The id a community list travels under.</summary>
	public static string WorkshopId( ulong fileId ) => WorkshopPrefix + fileId;

	/// <summary>True (and the file id) when <paramref name="id"/> names a community list.</summary>
	public static bool TryWorkshopId( string id, out ulong fileId )
	{
		fileId = 0;
		return id is not null && id.StartsWith( WorkshopPrefix, StringComparison.Ordinal )
			&& ulong.TryParse( id.AsSpan( WorkshopPrefix.Length ), out fileId );
	}
}

/// <summary>One selectable topic as the lobby shows it: a built-in pack's chip, or an added community list.</summary>
public readonly record struct CharadesTopicInfo( string Id, string Title, int PhraseCount, bool Workshop );

/// <summary>
/// The Charades phrase pool — sourced from <see cref="CharadesWordList"/> assets via ResourceLibrary (the
/// shipped topics; create and edit lists in the editor, not in code) plus any community lists the host has
/// added (<see cref="CharadesWorkshop"/>) — and the string rules everyone has to agree on: how a phrase is
/// masked for the guessers' hint, how a guess is normalised before comparing, how lenient the match is, and
/// what counts as a valid player-written phrase. The host validates guesses, but the HUD may someday want a
/// local "you're close" pre-check, so the rules live here rather than in the manager. The built-in lists
/// live under Assets/Charades/*.words.
/// </summary>
public static class CharadesWords
{
	/// <summary>Every built-in topic the shipped lists declare, one entry per distinct topic name (several
	/// lists can feed one), in asset-title order. Hidden lists don't count. The lobby's chips.</summary>
	public static List<CharadesTopicInfo> BuiltInTopics()
	{
		var byId = new Dictionary<string, (string Title, HashSet<string> Phrases)>( StringComparer.Ordinal );

		foreach ( var list in ResourceLibrary.GetAll<CharadesWordList>().OrderBy( l => l?.Title, StringComparer.OrdinalIgnoreCase ) )
		{
			if ( list is null || list.Hidden || string.IsNullOrWhiteSpace( list.TopicName ) )
				continue;

			var id = CharadesTopics.IdFor( list.TopicName );
			if ( id.Length == 0 )
				continue;

			if ( !byId.TryGetValue( id, out var slot ) )
				byId[id] = slot = (list.TopicName, new HashSet<string>( StringComparer.Ordinal ));

			foreach ( var word in list.Words ?? Enumerable.Empty<string>() )
			{
				var norm = Normalize( word );
				if ( norm.Length > 0 )
					slot.Phrases.Add( norm );
			}
		}

		return byId
			.OrderBy( kv => kv.Value.Title, StringComparer.OrdinalIgnoreCase )
			.Select( kv => new CharadesTopicInfo( kv.Key, kv.Value.Title, kv.Value.Phrases.Count, false ) )
			.ToList();
	}

	/// <summary>Everything selectable right now: the built-in topics, then the community lists this machine
	/// has added. Workshop lists come from the local cache — nothing here awaits.</summary>
	public static List<CharadesTopicInfo> AvailableTopics()
	{
		var topics = BuiltInTopics();
		foreach ( var list in CharadesWorkshop.Added )
			topics.Add( new CharadesTopicInfo( CharadesTopics.WorkshopId( list.FileId ), list.Title, list.Phrases.Count, true ) );
		return topics;
	}

	/// <summary>Every phrase the given selection allows: the union of every non-hidden built-in list whose
	/// topic is selected plus every selected community list, de-duplicated by normalised form (two lists both
	/// containing "duck" is one "duck"). The empty selection reads as every built-in topic — and a selection
	/// that resolves to nothing at all (a deleted asset, a community list that isn't installed here) falls back
	/// the same way, so a game can never start with zero phrases to draw from.</summary>
	public static List<string> PoolFor( string selection )
	{
		var ids = CharadesTopics.Parse( selection );
		var everything = ids.Count == 0;

		var pool = new List<string>();
		var seen = new HashSet<string>( StringComparer.Ordinal );

		void Add( string phrase )
		{
			var norm = Normalize( phrase );
			if ( norm.Length > 0 && seen.Add( norm ) )
				pool.Add( phrase.Trim() );
		}

		foreach ( var list in ResourceLibrary.GetAll<CharadesWordList>() )
		{
			if ( list is null || list.Hidden )
				continue;

			if ( !everything && !ids.Contains( CharadesTopics.IdFor( list.TopicName ) ) )
				continue;

			foreach ( var word in list.Words ?? Enumerable.Empty<string>() )
				Add( word );
		}

		if ( !everything )
		{
			foreach ( var id in ids )
			{
				if ( CharadesTopics.TryWorkshopId( id, out var fileId ) && CharadesWorkshop.TryGet( fileId, out var list ) )
					foreach ( var phrase in list.Phrases )
						Add( phrase );
			}
		}

		// A selection that names only things we don't have (or a stale id) — everything beats nothing.
		if ( pool.Count == 0 && !everything )
		{
			Log.Warning( $"CharadesWords: topic selection '{selection}' matched no lists — using every built-in topic." );
			return PoolFor( CharadesTopics.Everything );
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

	/// <summary>Draw <paramref name="count"/> distinct phrases from the selection, avoiding anything in
	/// <paramref name="used"/> until the pool runs dry (then <paramref name="used"/> is cleared and everything
	/// is fair game again — a long session repeating beats a session that stalls).</summary>
	public static List<string> Draw( int count, string selection, HashSet<string> used )
	{
		var pool = PoolFor( selection );

		var fresh = pool.Where( w => !used.Contains( w ) ).ToList();
		if ( fresh.Count < count )
		{
			used.Clear();
			fresh = pool;
		}

		var drawn = fresh.OrderBy( _ => Random.Shared.Next() ).Take( count ).ToList();

		// A tiny pool (assets missing, or a one-word custom list) still has to fill the mimic's offer slots —
		// repeat rather than hand BeginTurn a short list it will index past.
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

	// Filler words a guess may drop or add without changing what was meant: "mario on the beach" for
	// "mario sunbathing on the beach" is still a miss (a content word is gone), but "mario sunbathing on a
	// beach" is a hit. Kept short on purpose — matching should feel fair, not magic.
	static readonly HashSet<string> Articles = new( StringComparer.Ordinal ) { "a", "an", "the" };

	/// <summary>Does <paramref name="guess"/> count as <paramref name="answer"/>? Exact after
	/// <see cref="Normalize"/>, or equal once articles are dropped from both, or within a little typo slack
	/// (one edit per eight characters of the answer — "elefant" for "elephant", "octopuss" for "octopus";
	/// never for very short answers, where one letter IS the difference between words).</summary>
	public static bool Matches( string guess, string answer )
	{
		var g = Normalize( guess );
		var a = Normalize( answer );
		if ( g.Length == 0 || a.Length == 0 )
			return false;

		if ( g == a )
			return true;

		var gWords = g.Split( ' ' ).Where( w => !Articles.Contains( w ) ).ToArray();
		var aWords = a.Split( ' ' ).Where( w => !Articles.Contains( w ) ).ToArray();
		var gCore = string.Join( ' ', gWords );
		var aCore = string.Join( ' ', aWords );
		if ( gCore.Length > 0 && gCore == aCore )
			return true;

		var slack = aCore.Length / 8;
		if ( slack == 0 )
			return false;

		return EditDistance( gCore, aCore, slack ) <= slack;
	}

	// Levenshtein with an early out once the distance can't come back under the cap.
	static int EditDistance( string s, string t, int cap )
	{
		if ( Math.Abs( s.Length - t.Length ) > cap )
			return cap + 1;

		var prev = new int[t.Length + 1];
		var cur = new int[t.Length + 1];
		for ( var j = 0; j <= t.Length; j++ )
			prev[j] = j;

		for ( var i = 1; i <= s.Length; i++ )
		{
			cur[0] = i;
			var rowMin = cur[0];
			for ( var j = 1; j <= t.Length; j++ )
			{
				var cost = s[i - 1] == t[j - 1] ? 0 : 1;
				cur[j] = Math.Min( Math.Min( cur[j - 1] + 1, prev[j] + 1 ), prev[j - 1] + cost );
				rowMin = Math.Min( rowMin, cur[j] );
			}

			if ( rowMin > cap )
				return cap + 1;

			(prev, cur) = (cur, prev);
		}

		return prev[t.Length];
	}

	/// <summary>Longest a player-written (or community-list) phrase may be. Long enough for a scene
	/// ("mario sunbathing on the beach"), short enough to fit the reveal strip and a chat line.</summary>
	public const int MaxPhraseLength = 60;

	/// <summary>Clean a player-typed or community-list phrase for play: trimmed, inner whitespace folded,
	/// capped at <see cref="MaxPhraseLength"/>. Null when nothing guessable survives (fewer than two letters or
	/// digits once normalised) — the caller tells the player to try again.</summary>
	public static string SanitizePhrase( string text )
	{
		if ( string.IsNullOrWhiteSpace( text ) )
			return null;

		var folded = string.Join( ' ', text.Split( (char[])null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) );
		if ( folded.Length > MaxPhraseLength )
			folded = folded[..MaxPhraseLength].TrimEnd();

		return Normalize( folded ).Count( char.IsLetterOrDigit ) >= 2 ? folded : null;
	}

	/// <summary>A community list's file format is as plain as it gets: one phrase per line. Blank lines and
	/// lines starting with '#' are skipped, each line is sanitised, duplicates (by normalised form) dropped.</summary>
	public static List<string> ParseLines( string text )
	{
		var phrases = new List<string>();
		var seen = new HashSet<string>( StringComparer.Ordinal );
		foreach ( var raw in (text ?? "").Split( '\n' ) )
		{
			var line = raw.Trim();
			if ( line.Length == 0 || line.StartsWith( '#' ) )
				continue;

			var phrase = SanitizePhrase( line );
			if ( phrase is not null && seen.Add( Normalize( phrase ) ) )
				phrases.Add( phrase );
		}
		return phrases;
	}
}
