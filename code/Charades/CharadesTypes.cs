using System;
using System.Globalization;

namespace Mimiclay;

/// <summary>
/// The phases of a Charades game, in loop order. Charades plays on a picked MAP like every other game
/// (lobby → <see cref="LobbyManager"/> launch → <see cref="RoundManagerSpawner"/> spawns
/// <see cref="CharadesManager"/>), and the manager walks this loop: wait for players → turns (choose a word,
/// sculpt it on the stage while everyone guesses, reveal) → first player to the target score → podium →
/// back to the lobby (or, on a direct Play, back to Waiting forever).
/// </summary>
public enum CharadesPhase
{
	/// <summary>Not enough players yet (or a direct-play game just ended). Everyone wanders; nothing is timed.</summary>
	Waiting,
	/// <summary>Warm-up countdown once enough players are in — free wander, nothing locked (unlike prop
	/// hunt's frozen Starting). Ends into the first <see cref="Choosing"/>.</summary>
	Starting,
	/// <summary>The next mimic is picking one of three offered words. Everyone else sees who's up.</summary>
	Choosing,
	/// <summary>The mimic sculpts on the stage; everyone else guesses by text. The turn's main clock.</summary>
	Sculpting,
	/// <summary>The word is revealed and the turn's scores are shown; the sculpt stays up to admire/mourn.</summary>
	TurnReveal,
	/// <summary>Someone reached the target score: final standings before the game hands back to the lobby.</summary>
	Podium,
}

/// <summary>How the next mimic is chosen after each turn — the lobby's "Mimic" setting.</summary>
public enum MimicRotation
{
	/// <summary>Everyone takes the stage in seat order, round and round.</summary>
	TakeTurns,
	/// <summary>The turn's FIRST correct guesser takes the stage next ("winner stays on"). Nobody guessed —
	/// or the winner would repeat because they were already up — falls back to seat order, so a game can
	/// never stall on one mimic.</summary>
	WinnerStaysOn,
}

/// <summary>The word-pool topics, multi-selectable in the lobby ("Everything" = all of them). Flags so the
/// whole selection travels as one int — synced on <see cref="LobbyManager.CharadesCfg"/> and couriered as a
/// number.</summary>
[Flags]
public enum CharadesTopics
{
	None = 0,
	Animals = 1,
	Food = 2,
	Objects = 4,
	Vehicles = 8,
	Nature = 16,
	Characters = 32,

	Everything = Animals | Food | Objects | Vehicles | Nature | Characters,
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

	/// <summary>Total score this game — guessing (faster = more) and being guessed as the mimic.</summary>
	public int Score;

	/// <summary>Join order — the host's monotonic seat counter. The take-turns rotation is ordered by this,
	/// so "who mimes next" is stable and fair no matter how NetDictionary happens to enumerate.</summary>
	public int Seat;

	/// <summary>Which spawn point this player's own machine uses when spawning its pawn (assigned by the host).</summary>
	public int SpawnIndex;

	/// <summary>What place this player's correct guess took THIS turn (1 = first). 0 = hasn't guessed it yet.
	/// Doubles as the "already scored, stop re-scoring" flag; cleared by the host at the start of each turn.</summary>
	public int GuessedPlace;

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
	/// <summary>First player to reach this score wins the game — the lobby's "First to: X".</summary>
	public int TargetScore;

	/// <summary>How the next mimic is picked (take turns / winner stays on).</summary>
	public MimicRotation Rotation;

	/// <summary>Which word-pool topics are in play. <see cref="CharadesTopics.Everything"/> = all.</summary>
	public CharadesTopics Topics;

	/// <summary>Show guessers the masked word shape ("_ _ _   _ _ _ _") during the sculpt. Off = no hint at
	/// all: the synced hint is simply never written, so nothing about the word's length reaches clients.</summary>
	public bool WordLengthHints;

	/// <summary>Warm-up countdown after enough players gather, before the first turn (free wander, no freeze).</summary>
	public float StartCountdownSeconds;

	/// <summary>How long the mimic has to pick one of the offered words before the first is auto-picked.</summary>
	public float ChooseSeconds;

	/// <summary>The sculpting/guessing time — the turn's main clock.</summary>
	public float SculptSeconds;

	/// <summary>How long the revealed word + turn scores linger before the next turn.</summary>
	public float RevealSeconds;

	/// <summary>How long the final standings show before the game returns to the lobby.</summary>
	public float PodiumSeconds;

	/// <summary>Players needed before a game starts. The map card's solo-debug toggle lowers this to 1.</summary>
	public int MinPlayers;

	// One place for defaults, mirrored as consts so [Property] initializers elsewhere can compile-time seed
	// from the same numbers (attribute arguments must be constant expressions — see RoundSettings for the
	// precedent and the s&box generated-attribute reason).
	public const int DefaultTargetScore = 15;
	public const MimicRotation DefaultRotation = MimicRotation.TakeTurns;
	public const CharadesTopics DefaultTopics = CharadesTopics.Everything;
	public const bool DefaultWordLengthHints = true;
	public const float DefaultStartCountdownSeconds = 10f;
	public const float DefaultChooseSeconds = 15f;
	public const float DefaultSculptSeconds = 150f;
	public const float DefaultRevealSeconds = 8f;
	public const float DefaultPodiumSeconds = 14f;
	public const int DefaultMinPlayers = 2;

	public static CharadesSettings Default => new()
	{
		TargetScore = DefaultTargetScore,
		Rotation = DefaultRotation,
		Topics = DefaultTopics,
		WordLengthHints = DefaultWordLengthHints,
		StartCountdownSeconds = DefaultStartCountdownSeconds,
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
		public const string Target = "ch.target";
		public const string Rotation = "ch.rot";
		public const string Topics = "ch.topics";
		public const string Hints = "ch.hints";
		public const string Start = "ch.start";
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
		Networking.SetData( Keys.Target, TargetScore.ToString( CultureInfo.InvariantCulture ) );
		Networking.SetData( Keys.Rotation, ((int)Rotation).ToString( CultureInfo.InvariantCulture ) );
		Networking.SetData( Keys.Topics, ((int)Topics).ToString( CultureInfo.InvariantCulture ) );
		Networking.SetData( Keys.Hints, WordLengthHints ? "1" : "0" );
		Networking.SetData( Keys.Start, Str( StartCountdownSeconds ) );
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

		if ( int.TryParse( Networking.GetData( Keys.Target ), NumberStyles.Integer, CultureInfo.InvariantCulture, out var target ) )
			d.TargetScore = Math.Max( 1, target );

		if ( int.TryParse( Networking.GetData( Keys.Rotation ), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rot )
			&& Enum.IsDefined( typeof( MimicRotation ), rot ) )
			d.Rotation = (MimicRotation)rot;

		if ( int.TryParse( Networking.GetData( Keys.Topics ), NumberStyles.Integer, CultureInfo.InvariantCulture, out var topics )
			&& (topics & (int)CharadesTopics.Everything) != 0 )
			d.Topics = (CharadesTopics)topics & CharadesTopics.Everything;

		var hints = Networking.GetData( Keys.Hints );
		if ( hints == "0" )
			d.WordLengthHints = false;
		else if ( hints == "1" )
			d.WordLengthHints = true;

		d.StartCountdownSeconds = Read( Keys.Start, d.StartCountdownSeconds );
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
		Networking.SetData( Keys.Target, "" );
		Networking.SetData( Keys.Rotation, "" );
		Networking.SetData( Keys.Topics, "" );
		Networking.SetData( Keys.Hints, "" );
		Networking.SetData( Keys.Start, "" );
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
