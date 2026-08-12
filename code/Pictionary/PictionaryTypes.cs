using System;
using System.Globalization;

namespace Mimiclay;

/// <summary>
/// The phases of a Pictionary session, in loop order. Unlike prop hunt there is no lobby/map scene split —
/// the whole session lives in one scene (<c>pictionary.scene</c>) and <see cref="PictionaryManager"/> walks
/// this loop forever: wait for players → each player takes a sculpting turn (choose a word, sculpt it while
/// everyone guesses, reveal) → podium → back to waiting.
/// </summary>
public enum PictionaryPhase
{
	/// <summary>Not enough players yet (or a game just ended). Everyone wanders the room; nothing is timed.</summary>
	Waiting,
	/// <summary>The next sculptor is picking one of three offered words. Everyone else sees who's up.</summary>
	Choosing,
	/// <summary>The sculptor shapes the canvas on the stage; everyone else watches and guesses by text.</summary>
	Sculpting,
	/// <summary>The word is revealed and the turn's scores are shown; the canvas stays up to admire/mourn.</summary>
	TurnReveal,
	/// <summary>All turns played: final standings before the loop returns to <see cref="Waiting"/>.</summary>
	Podium,
}

/// <summary>
/// Per-player session state, replicated via the manager's <c>NetDictionary&lt;Guid, PictionaryPlayer&gt;</c>
/// keyed by <see cref="Connection.Id"/> — same plain-primitives contract as prop hunt's <see cref="PlayerInfo"/>.
/// Rows outlive pawns and turns; scores accumulate across the whole game.
/// </summary>
public struct PictionaryPlayer
{
	/// <summary>The owning connection (dictionary key, duplicated so values are self-describing).</summary>
	public Guid Connection;

	/// <summary>Display name, snapshotted at join.</summary>
	public string Name;

	/// <summary>Total score this game (guessing fast + being guessed as sculptor).</summary>
	public int Score;

	/// <summary>Join order — the host's monotonic seat counter. The turn rotation is ordered by this, so
	/// "who sculpts next" is stable and fair no matter how NetDictionary happens to enumerate.</summary>
	public int Seat;

	/// <summary>Which spawn point this player uses when spawning their own pawn (assigned by the host).</summary>
	public int SpawnIndex;

	/// <summary>This player already guessed the current word this turn (stops re-scoring and lets the turn
	/// end early once everyone has it). Cleared by the host at the start of each turn.</summary>
	public bool GuessedThisTurn;
}

/// <summary>
/// The host-tunable rules for a Pictionary game. Two sources, in priority order:
/// <list type="bullet">
/// <item>Launched from the LOBBY: the host configured these in the setup dialog, and they ride session data
/// across the scene change (<see cref="WriteToLobby"/>/<see cref="ReadFromLobby"/> — the same courier pattern
/// as <see cref="RoundSettings"/>, <c>p.</c>-namespaced keys).</item>
/// <item>Direct Play on the pictionary scene: no courier keys exist, so the spawner's scene-authored values
/// stand (they're the fallback every missing key reads back as).</item>
/// </list>
/// </summary>
public struct PictionarySettings
{
	/// <summary>How long the sculptor has to pick one of the offered words before the first is auto-picked.</summary>
	public float ChooseSeconds;

	/// <summary>The sculpting/guessing time — the turn's main clock.</summary>
	public float SculptSeconds;

	/// <summary>How long the revealed word + turn scores linger before the next turn.</summary>
	public float RevealSeconds;

	/// <summary>How long the final standings show before the loop returns to Waiting.</summary>
	public float PodiumSeconds;

	/// <summary>How many full cycles (everyone sculpts once per cycle) make a game.</summary>
	public int Rounds;

	/// <summary>Players needed before a game starts. Debug solo lowers this to 1 (see the spawner).</summary>
	public int MinPlayers;

	// One place for defaults, mirrored as consts so spawner [Property] initializers can compile-time seed
	// from the same numbers (attribute arguments must be constant expressions — see RoundSettings for the
	// precedent and the s&box generated-attribute reason).
	public const float DefaultChooseSeconds = 15f;
	public const float DefaultSculptSeconds = 150f;
	public const float DefaultRevealSeconds = 8f;
	public const float DefaultPodiumSeconds = 14f;
	public const int DefaultRounds = 2;
	public const int DefaultMinPlayers = 2;

	public static PictionarySettings Default => new()
	{
		ChooseSeconds = DefaultChooseSeconds,
		SculptSeconds = DefaultSculptSeconds,
		RevealSeconds = DefaultRevealSeconds,
		PodiumSeconds = DefaultPodiumSeconds,
		Rounds = DefaultRounds,
		MinPlayers = DefaultMinPlayers,
	};

	// ── Lobby-data courier (see RoundSettings for the pattern + why: ChangeScene destroys the lobby scene,
	// session data is the engine static that survives it) ─────────────────────────────────────────────────
	static class Keys
	{
		public const string Choose = "p.choose";
		public const string Sculpt = "p.sculpt";
		public const string Reveal = "p.reveal";
		public const string Podium = "p.podium";
		public const string Rounds = "p.rounds";
		/// <summary>"1" when the lobby launched this pictionary session — the manager returns to the lobby
		/// after the podium. Absent on a direct Play, which loops in-scene forever like a sandbox.</summary>
		public const string Launched = "p.go";
	}

	/// <summary>True when the running pictionary scene was launched from the lobby (so it should return there).</summary>
	public static bool CameFromLobby => Networking.GetData( Keys.Launched ) == "1";

	/// <summary>Host-only: flatten these settings into session data right before the lobby's ChangeScene.</summary>
	public readonly void WriteToLobby()
	{
		Networking.SetData( Keys.Choose, Str( ChooseSeconds ) );
		Networking.SetData( Keys.Sculpt, Str( SculptSeconds ) );
		Networking.SetData( Keys.Reveal, Str( RevealSeconds ) );
		Networking.SetData( Keys.Podium, Str( PodiumSeconds ) );
		Networking.SetData( Keys.Rounds, Rounds.ToString( CultureInfo.InvariantCulture ) );
		Networking.SetData( Keys.Launched, "1" );
	}

	/// <summary>Read the settings back inside the pictionary scene, falling back per-key to
	/// <paramref name="fallback"/> — the spawner's scene-authored values, so a direct Play (no keys) is
	/// entirely the spawner's config. MinPlayers always stays the fallback's: the lobby launches with the
	/// crowd it has, so it isn't a lobby-configured rule.</summary>
	public static PictionarySettings ReadFromLobby( PictionarySettings fallback )
	{
		var d = fallback;
		d.ChooseSeconds = Read( Keys.Choose, d.ChooseSeconds );
		d.SculptSeconds = Read( Keys.Sculpt, d.SculptSeconds );
		d.RevealSeconds = Read( Keys.Reveal, d.RevealSeconds );
		d.PodiumSeconds = Read( Keys.Podium, d.PodiumSeconds );

		if ( int.TryParse( Networking.GetData( Keys.Rounds ), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r ) )
			d.Rounds = Math.Max( 1, r );

		return d;
	}

	/// <summary>Blank every pictionary courier key. Self-host bootstraps call this: session data survives the
	/// editor's Stop→Play, so without it a direct Play after a lobby-launched pictionary game in the same
	/// editor run would read the old lobby's rules — and, worse, see <see cref="Keys.Launched"/> and try to
	/// "return" to a lobby it never came from.</summary>
	public static void ClearLobbyData()
	{
		Networking.SetData( Keys.Choose, "" );
		Networking.SetData( Keys.Sculpt, "" );
		Networking.SetData( Keys.Reveal, "" );
		Networking.SetData( Keys.Podium, "" );
		Networking.SetData( Keys.Rounds, "" );
		Networking.SetData( Keys.Launched, "" );
	}

	static string Str( float v ) => v.ToString( "0.###", CultureInfo.InvariantCulture );

	static float Read( string key, float fallback )
		=> float.TryParse( Networking.GetData( key ), NumberStyles.Float, CultureInfo.InvariantCulture, out var v )
			? v
			: fallback;
}
