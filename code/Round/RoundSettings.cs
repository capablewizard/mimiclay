using System;
using System.Globalization;

namespace Mimiclay;

/// <summary>
/// The host-tunable rules for a round: phase durations, how many hunters, and the chosen map. Lives in two places
/// that have to agree:
///   • In the lobby it's a <c>[Sync]</c> field on <see cref="LobbyController"/> the host edits and clients watch.
///   • Across the scene change into the map it's flattened into session lobby data (<see cref="WriteToLobby"/>),
///     because s&amp;box destroys the whole scene — and every component on it — when the host calls
///     <see cref="Game.ChangeScene"/>. <see cref="Networking"/> data lives on the static session, not the scene,
///     so it's the natural courier. <see cref="RoundManager"/> reads it back with <see cref="ReadFromLobby"/>.
///
/// Stored as a handful of short string keys (Steam lobby data is small + string-only), parsed culture-invariantly.
/// Durations are whole seconds — fine granularity for a block-out and keeps the wire format trivial.
/// </summary>
public struct RoundSettings
{
	/// <summary>The round rules variant (Infection / Teams …). See <see cref="RoundMode"/>.</summary>
	public RoundMode Mode;

	public float StartCountdownSeconds;
	public float HideSeconds;
	public float HuntSeconds;
	public float RevealSeconds;
	public float ConsolidationSeconds;

	/// <summary>How often (seconds) each surviving prop automatically taunts during the Hunt — the whistle every
	/// hunter hears from the prop's hiding spot. Individual props stagger around this period so the whole lobby
	/// never whistles in chorus (see <see cref="HiderController"/>). 0 = no automatic taunts (manual T still works).</summary>
	public float TauntSeconds;

	/// <summary>How often (seconds) each surviving prop's HUD pip briefly flashes a dark silhouette of their actual
	/// disguise during the Hunt — the "who's that pokemon" hint. Per-prop staggered (deterministically, so every
	/// machine flashes the same prop at the same moment — see <see cref="RoundHud"/>). 0 = never.</summary>
	public float HintSeconds;

	/// <summary>How many players are made hunters at round start (the rest are props). Clamped to leave ≥1 prop.</summary>
	public int HunterCount;

	/// <summary>The chosen map's <c>ResourcePath</c> (a <see cref="MapResource"/>), or <see cref="MapCatalog.RandomIdent"/>.</summary>
	public string MapIdent;

	// Sensible opening values — short hide, longer hunt, brief reveal/consolidation, one hunter. Held as CONSTS
	// rather than written straight into Default below so that inspector properties elsewhere can seed themselves
	// from the same numbers (see RoundManagerSpawner's rules override). s&box bakes a [Property]'s initializer
	// into a generated attribute, and an attribute argument has to be a constant expression — a `Default.HideSeconds`
	// read is not one, and fails to compile. One place to change a default, no drift.
	public const RoundMode DefaultMode = RoundMode.Infection;
	public const float DefaultStartCountdownSeconds = 4f;
	public const float DefaultHideSeconds = 90f;
	public const float DefaultHuntSeconds = 210f;
	public const float DefaultRevealSeconds = 30f;
	public const float DefaultConsolidationSeconds = 12f;
	public const float DefaultTauntSeconds = 35f;
	public const float DefaultHintSeconds = 60f;
	public const int DefaultHunterCount = 1;

	/// <summary>Sensible opening values — short hide, longer hunt, brief reveal/consolidation, one hunter.</summary>
	public static RoundSettings Default => new()
	{
		Mode = DefaultMode,
		StartCountdownSeconds = DefaultStartCountdownSeconds,
		HideSeconds = DefaultHideSeconds,
		HuntSeconds = DefaultHuntSeconds,
		RevealSeconds = DefaultRevealSeconds,
		ConsolidationSeconds = DefaultConsolidationSeconds,
		TauntSeconds = DefaultTauntSeconds,
		HintSeconds = DefaultHintSeconds,
		HunterCount = DefaultHunterCount,
		MapIdent = MapCatalog.RandomIdent,
	};

	// ── Lobby-data courier ────────────────────────────────────────────────────────────────────────────────
	// Short keys, namespaced so they don't collide with MenuNetworking.Keys (mode/name).
	static class Keys
	{
		public const string Mode = "r.mode";
		public const string Start = "r.start";
		public const string Hide = "r.hide";
		public const string Hunt = "r.hunt";
		public const string Reveal = "r.reveal";
		public const string Cons = "r.cons";
		public const string Taunt = "r.taunt";
		public const string Hint = "r.hint";
		public const string Hunters = "r.hn";
		public const string Map = "r.map";
	}

	/// <summary>Host-only: flatten these settings into session data so they survive the scene change into the map.
	/// Call right before <see cref="Game.ChangeScene"/>. The resolved map ident (Random already rolled into a real
	/// map) should be passed in so every client agrees on the same scene.</summary>
	public readonly void WriteToLobby( string resolvedMapIdent )
	{
		Networking.SetData( Keys.Mode, ((int)Mode).ToString( CultureInfo.InvariantCulture ) );
		Networking.SetData( Keys.Start, Str( StartCountdownSeconds ) );
		Networking.SetData( Keys.Hide, Str( HideSeconds ) );
		Networking.SetData( Keys.Hunt, Str( HuntSeconds ) );
		Networking.SetData( Keys.Reveal, Str( RevealSeconds ) );
		Networking.SetData( Keys.Cons, Str( ConsolidationSeconds ) );
		Networking.SetData( Keys.Taunt, Str( TauntSeconds ) );
		Networking.SetData( Keys.Hint, Str( HintSeconds ) );
		Networking.SetData( Keys.Hunters, HunterCount.ToString( CultureInfo.InvariantCulture ) );
		Networking.SetData( Keys.Map, resolvedMapIdent ?? "" );
	}

	/// <summary>Read the settings back out of session data in the map scene (any machine). Missing keys fall back to
	/// <see cref="Default"/>, so a map scene opened directly in the editor still gets usable values.</summary>
	public static RoundSettings ReadFromLobby()
	{
		var d = Default;

		var mode = Networking.GetData( Keys.Mode );
		if ( int.TryParse( mode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m ) && Enum.IsDefined( typeof( RoundMode ), m ) )
			d.Mode = (RoundMode)m;

		d.StartCountdownSeconds = Read( Keys.Start, d.StartCountdownSeconds );
		d.HideSeconds = Read( Keys.Hide, d.HideSeconds );
		d.HuntSeconds = Read( Keys.Hunt, d.HuntSeconds );
		d.RevealSeconds = Read( Keys.Reveal, d.RevealSeconds );
		d.ConsolidationSeconds = Read( Keys.Cons, d.ConsolidationSeconds );
		d.TauntSeconds = Read( Keys.Taunt, d.TauntSeconds );
		d.HintSeconds = Read( Keys.Hint, d.HintSeconds );

		var hunters = Networking.GetData( Keys.Hunters );
		if ( int.TryParse( hunters, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n ) )
			d.HunterCount = n;

		var map = Networking.GetData( Keys.Map );
		if ( !string.IsNullOrEmpty( map ) )
			d.MapIdent = map;

		return d;
	}

	/// <summary>Blank EVERY key a lobby launch writes — the rules above plus the roster couriers
	/// (<see cref="RoundManager.HunterIdsKey"/>, <see cref="RoundManager.BotCountKey"/>). For a session this
	/// process creates ITSELF (a map scene opened directly): session data lives on an engine static that
	/// only a process restart clears, so without this a lobby launch earlier in the same editor run leaves
	/// its keys behind — and the fresh "direct play" session reads the old lobby's rules and bot count
	/// (typically 0, silently eating the spawner's test bots) instead of the map's own config. Blank is
	/// how a key reads as missing here: every reader falls back on a failed parse.</summary>
	public static void ClearLobbyData()
	{
		// The GAME key too (MenuNetworking.Keys.Mode — the launch stamps it as the map's game selector): a
		// Creative launch earlier in the same editor run would otherwise flip a direct-played map scene into
		// sandbox mode. Blank reads fall back to PropHunt.
		Networking.SetData( MenuNetworking.Keys.Mode, "" );

		Networking.SetData( Keys.Mode, "" );
		Networking.SetData( Keys.Start, "" );
		Networking.SetData( Keys.Hide, "" );
		Networking.SetData( Keys.Hunt, "" );
		Networking.SetData( Keys.Reveal, "" );
		Networking.SetData( Keys.Cons, "" );
		Networking.SetData( Keys.Taunt, "" );
		Networking.SetData( Keys.Hint, "" );
		Networking.SetData( Keys.Hunters, "" );
		Networking.SetData( Keys.Map, "" );
		Networking.SetData( RoundManager.HunterIdsKey, "" );
		Networking.SetData( RoundManager.BotCountKey, "" );
	}

	static string Str( float v ) => v.ToString( "0.###", CultureInfo.InvariantCulture );

	static float Read( string key, float fallback )
		=> float.TryParse( Networking.GetData( key ), NumberStyles.Float, CultureInfo.InvariantCulture, out var v )
			? v
			: fallback;
}
