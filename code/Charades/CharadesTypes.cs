using System;
using System.Globalization;

namespace Mimiclay;

/// <summary>
/// The phases of a Charades game, in loop order. Charades plays on a picked MAP like every other game
/// (lobby → <see cref="LobbyManager"/> launch → <see cref="RoundManagerSpawner"/> spawns
/// <see cref="CharadesManager"/>), and the manager walks this loop: wait for players → rounds (everyone takes
/// one turn per round: pick/read a phrase, sculpt it on the stage while everyone guesses, reveal) for the
/// configured number of rounds → podium → back to the lobby (or, on a direct Play, back to Waiting forever).
/// </summary>
public enum CharadesPhase
{
	/// <summary>Not enough players yet (or a direct-play game just ended). Everyone wanders; nothing is timed.</summary>
	Waiting,
	/// <summary>Warm-up countdown once enough players are in — free wander, nothing locked (unlike prop
	/// hunt's frozen Starting). Ends into the first round.</summary>
	Starting,
	/// <summary>Players-write-the-phrases rounds only: everyone types a phrase (in chat — the host swallows
	/// it) for someone ELSE to sculpt this round. Ends when the clock runs out or everyone has submitted.</summary>
	Writing,
	/// <summary>The next mimic is picking one of the offered phrases (or reading the one written for them).
	/// Everyone else sees who's up.</summary>
	Choosing,
	/// <summary>The mimic sculpts on the stage; everyone else guesses by text. The turn's main clock.</summary>
	Sculpting,
	/// <summary>The phrase is revealed and the turn's scores are shown; the sculpt stays up to admire/mourn.</summary>
	TurnReveal,
	/// <summary>The last round is done: final standings before the game hands back to the lobby.</summary>
	Podium,
}

/// <summary>Where each turn's phrase comes from — the lobby's "Phrases" setting.</summary>
public enum PhraseSource
{
	/// <summary>Drawn from the ticked topics: the Standard (built-in) ones plus any ticked Community (Steam
	/// Workshop) lists. With <see cref="CharadesSettings.TopicChoices"/> the
	/// mimic picks one of three; without, they're handed one.</summary>
	Topics,
	/// <summary>Written by the players ("Write Your Own"): at the start of every round everyone submits a
	/// phrase, and each is handed to someone else to sculpt. Anyone who doesn't write gets a random built-in
	/// phrase as filler.</summary>
	Players,
}

/// <summary>
/// Per-player game state, replicated via the manager's <c>NetDictionary&lt;Guid, CharadesPlayer&gt;</c> keyed
/// by <see cref="Connection.Id"/> (or a <see cref="RoundBots"/> seat id) — same plain-primitives contract as
/// prop hunt's <see cref="PlayerInfo"/>. Rows outlive pawns and turns; scores accumulate across the game.
/// </summary>
public struct CharadesPlayer
{
	/// <summary>The owning connection or bot seat (dictionary key, duplicated so values are self-describing).</summary>
	public Guid Connection;

	/// <summary>Display name, snapshotted at join.</summary>
	public string Name;

	/// <summary>Total score this game — guessing (faster = more), being guessed as the mimic, and (players-write
	/// rounds) having the phrase you wrote guessed.</summary>
	public int Score;

	/// <summary>Join order — the host's monotonic seat counter. The take-turns rotation is ordered by this,
	/// so "who mimes next" is stable and fair no matter how NetDictionary happens to enumerate.</summary>
	public int Seat;

	/// <summary>Which spawn point this player's own machine uses when spawning its pawn (assigned by the host).</summary>
	public int SpawnIndex;

	/// <summary>What place this player's correct guess took THIS turn (1 = first). 0 = hasn't guessed it yet.
	/// Doubles as the "already scored, stop re-scoring" flag; cleared by the host at the start of each turn.</summary>
	public int GuessedPlace;

	/// <summary>Players-write rounds: this player has handed in their phrase for the current round (the
	/// roster's ✏️ badge, and what ends Writing early once everyone has one).</summary>
	public bool Submitted;

	/// <summary>A test-bot row — no machine behind it, the host holds its body (see <see cref="RoundBots"/>).</summary>
	public bool Bot;
}

/// <summary>
/// The host-tunable Charades rules. Two lives, exactly like <see cref="RoundSettings"/>: a <c>[Sync]</c>
/// field on <see cref="LobbyManager"/> while the host configures it, then flattened into session lobby data
/// at launch (<see cref="WriteToLobby"/> — the scene change destroys the lobby scene) and read back by
/// <see cref="CharadesManager"/> in the map (<see cref="ReadFromLobby"/>). A direct Play has no keys, so
/// everything falls back to <see cref="Default"/> — or the map card's override (see <see cref="MapModeCard"/>).
/// </summary>
public struct CharadesSettings
{
	/// <summary>How many rounds to play — a round is one turn on the stage for everyone seated when it starts.
	/// The highest score after the last round wins.</summary>
	public int Rounds;

	/// <summary>Where phrases come from: the ticked topics (standard + community), or written by the players.</summary>
	public PhraseSource Source;

	/// <summary>Topics source: which Standard (built-in) topics feed the pool, as comma-joined topic ids
	/// ("animals,food"; see <see cref="CharadesTopics"/>). Empty = every built-in topic; <see cref="CharadesTopics.NoStandard"/>
	/// = none of them (only allowed while a Community list is ticked).</summary>
	public string Topics;

	/// <summary>Topics source: offer the mimic three phrases to pick from (Yes), or hand them one (No).</summary>
	public bool TopicChoices;

	/// <summary>Topics source: the ticked Community lists, as comma-joined "ws:&lt;fileId&gt;" ids. They're drawn
	/// from alongside the Standard topics.</summary>
	public string WorkshopLists;

	/// <summary>Show guessers the masked phrase shape ("_ _ _   _ _ _ _") during the sculpt. Off = no hint at
	/// all: the synced hint is simply never written, so nothing about the phrase's length reaches clients.</summary>
	public bool WordLengthHints;

	/// <summary>Warm-up countdown after enough players gather, before the first round (free wander, no freeze).</summary>
	public float StartCountdownSeconds;

	/// <summary>Players-write rounds: how long everyone has to type their phrase before fillers are drawn.</summary>
	public float WriteSeconds;

	/// <summary>How long the mimic has to pick one of the offered phrases before the first is auto-picked
	/// (players-write rounds: how long they get to read theirs before the sculpt opens).</summary>
	public float ChooseSeconds;

	/// <summary>The sculpting/guessing time — the turn's main clock.</summary>
	public float SculptSeconds;

	/// <summary>How long the revealed phrase + turn scores linger before the next turn.</summary>
	public float RevealSeconds;

	/// <summary>How long the final standings show before the game returns to the lobby.</summary>
	public float PodiumSeconds;

	/// <summary>Players needed before a game starts. The map card's solo-debug toggle lowers this to 1.</summary>
	public int MinPlayers;

	// One place for defaults, mirrored as consts so [Property] initializers elsewhere can compile-time seed
	// from the same numbers (attribute arguments must be constant expressions — see RoundSettings for the
	// precedent and the s&box generated-attribute reason).
	public const int DefaultRounds = 2;
	public const PhraseSource DefaultSource = PhraseSource.Topics;
	public const string DefaultTopics = CharadesTopics.Everything;
	public const bool DefaultTopicChoices = true;
	public const string DefaultWorkshopLists = "";
	public const bool DefaultWordLengthHints = true;
	public const float DefaultStartCountdownSeconds = 10f;
	public const float DefaultWriteSeconds = 45f;
	public const float DefaultChooseSeconds = 10f;
	public const float DefaultSculptSeconds = 150f;
	public const float DefaultRevealSeconds = 10f;
	public const float DefaultPodiumSeconds = 14f;
	public const int DefaultMinPlayers = 2;

	public const int MinRounds = 1;
	public const int MaxRounds = 10;

	public static CharadesSettings Default => new()
	{
		Rounds = DefaultRounds,
		Source = DefaultSource,
		Topics = DefaultTopics,
		TopicChoices = DefaultTopicChoices,
		WorkshopLists = DefaultWorkshopLists,
		WordLengthHints = DefaultWordLengthHints,
		StartCountdownSeconds = DefaultStartCountdownSeconds,
		WriteSeconds = DefaultWriteSeconds,
		ChooseSeconds = DefaultChooseSeconds,
		SculptSeconds = DefaultSculptSeconds,
		RevealSeconds = DefaultRevealSeconds,
		PodiumSeconds = DefaultPodiumSeconds,
		MinPlayers = DefaultMinPlayers,
	};

	// ── Lobby-data courier (see RoundSettings for the pattern + why) ──────────────────────────────────────
	// "ch." keys — "c." belongs to CreativeSettings, "r." to RoundSettings.
	static class Keys
	{
		public const string Rounds = "ch.rounds";
		public const string Source = "ch.source";
		public const string Topics = "ch.topics";
		public const string Choices = "ch.choices";
		public const string WorkshopLists = "ch.ws";
		public const string Hints = "ch.hints";
		public const string Start = "ch.start";
		public const string Write = "ch.write";
		public const string Choose = "ch.choose";
		public const string Sculpt = "ch.sculpt";
		public const string Reveal = "ch.reveal";
		public const string Podium = "ch.podium";
		/// <summary>"1" when the lobby launched this charades game — the manager returns to the lobby after
		/// the podium. Absent on a direct Play, which loops in-scene forever for solo testing.</summary>
		public const string Launched = "ch.go";
	}

	/// <summary>True when the running charades game was launched from the lobby (so it should return there).</summary>
	public static bool CameFromLobby => Networking.GetData( Keys.Launched ) == "1";

	/// <summary>Host-only: flatten these settings into session data right before the lobby's ChangeScene.</summary>
	public readonly void WriteToLobby()
	{
		Networking.SetData( Keys.Rounds, Rounds.ToString( CultureInfo.InvariantCulture ) );
		Networking.SetData( Keys.Source, ((int)Source).ToString( CultureInfo.InvariantCulture ) );
		Networking.SetData( Keys.Topics, Topics ?? "" );
		Networking.SetData( Keys.Choices, TopicChoices ? "1" : "0" );
		Networking.SetData( Keys.WorkshopLists, WorkshopLists ?? "" );
		Networking.SetData( Keys.Hints, WordLengthHints ? "1" : "0" );
		Networking.SetData( Keys.Start, Str( StartCountdownSeconds ) );
		Networking.SetData( Keys.Write, Str( WriteSeconds ) );
		Networking.SetData( Keys.Choose, Str( ChooseSeconds ) );
		Networking.SetData( Keys.Sculpt, Str( SculptSeconds ) );
		Networking.SetData( Keys.Reveal, Str( RevealSeconds ) );
		Networking.SetData( Keys.Podium, Str( PodiumSeconds ) );
		Networking.SetData( Keys.Launched, "1" );
	}

	/// <summary>Read the settings back inside the map scene, falling back per-key to <paramref name="fallback"/>
	/// (the card override on a direct Play, or plain <see cref="Default"/>). MinPlayers always stays the
	/// fallback's: the lobby launches with the crowd it has, so it isn't a lobby-configured rule.</summary>
	public static CharadesSettings ReadFromLobby( CharadesSettings fallback )
	{
		var d = fallback;

		if ( int.TryParse( Networking.GetData( Keys.Rounds ), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rounds ) )
			d.Rounds = Math.Clamp( rounds, MinRounds, MaxRounds );

		if ( int.TryParse( Networking.GetData( Keys.Source ), NumberStyles.Integer, CultureInfo.InvariantCulture, out var src )
			&& Enum.IsDefined( typeof( PhraseSource ), src ) )
			d.Source = (PhraseSource)src;

		// The lobby wrote the key (possibly as "" = everything) only if it launched us — a direct Play has no
		// key at all and keeps the fallback's selection. GetData returns null for an absent key.
		var topics = Networking.GetData( Keys.Topics );
		if ( topics is not null && CameFromLobby )
			d.Topics = topics;

		var ws = Networking.GetData( Keys.WorkshopLists );
		if ( ws is not null && CameFromLobby )
			d.WorkshopLists = ws;

		var choices = Networking.GetData( Keys.Choices );
		if ( choices == "0" )
			d.TopicChoices = false;
		else if ( choices == "1" )
			d.TopicChoices = true;

		var hints = Networking.GetData( Keys.Hints );
		if ( hints == "0" )
			d.WordLengthHints = false;
		else if ( hints == "1" )
			d.WordLengthHints = true;

		d.StartCountdownSeconds = Read( Keys.Start, d.StartCountdownSeconds );
		d.WriteSeconds = Read( Keys.Write, d.WriteSeconds );
		d.ChooseSeconds = Read( Keys.Choose, d.ChooseSeconds );
		d.SculptSeconds = Read( Keys.Sculpt, d.SculptSeconds );
		d.RevealSeconds = Read( Keys.Reveal, d.RevealSeconds );
		d.PodiumSeconds = Read( Keys.Podium, d.PodiumSeconds );

		return d;
	}

	/// <summary>Blank every charades courier key. The self-host bootstraps call this beside the RoundSettings
	/// and CreativeSettings clears: session data survives the editor's Stop→Play, so a lobby-launched charades
	/// game earlier in the same editor run would otherwise leave its rules — and, worse, the came-from-lobby
	/// flag, making a direct Play try to "return" to a lobby it never came from.</summary>
	public static void ClearLobbyData()
	{
		Networking.SetData( Keys.Rounds, "" );
		Networking.SetData( Keys.Source, "" );
		Networking.SetData( Keys.Topics, "" );
		Networking.SetData( Keys.Choices, "" );
		Networking.SetData( Keys.WorkshopLists, "" );
		Networking.SetData( Keys.Hints, "" );
		Networking.SetData( Keys.Start, "" );
		Networking.SetData( Keys.Write, "" );
		Networking.SetData( Keys.Choose, "" );
		Networking.SetData( Keys.Sculpt, "" );
		Networking.SetData( Keys.Reveal, "" );
		Networking.SetData( Keys.Podium, "" );
		Networking.SetData( Keys.Launched, "" );
	}

	static string Str( float v ) => v.ToString( "0.###", CultureInfo.InvariantCulture );

	static float Read( string key, float fallback )
		=> float.TryParse( Networking.GetData( key ), NumberStyles.Float, CultureInfo.InvariantCulture, out var v )
			? v
			: fallback;
}
