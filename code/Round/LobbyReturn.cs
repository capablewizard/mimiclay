using System;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// The shared "Returning to Lobby" beat every game mode ends on. Instead of a manager flipping the scene the
/// instant its last phase expires (or the host's pause-menu button doing it mid-game), the host arms this
/// countdown; for <see cref="Seconds"/> (five seconds) the game state is FROZEN — no phase transitions, no scoring, no shots
/// landing, no guesses judged — every HUD shows the same <c>"Returning to Lobby"</c> readout (the lobby's own
/// launch countdown, via <c>Countdown.razor</c>), and then the host changes scene for the whole session.
///
/// Rides the mode manager's GameObject (<see cref="RoundManagerSpawner"/> creates it beside <see cref="RoundManager"/>,
/// <see cref="CharadesManager"/> and <see cref="CreativeManager"/>), so the NetworkSpawn that ships the manager
/// ships this too and its <c>[Sync]</c> state reaches clients and late-joiners through the same snapshot — a
/// scene-placed component's [Sync] changes don't replicate here. One copy per game scene; none in the lobby.
///
/// Who arms it: the managers when their final phase runs out (prop hunt's Consolidation, charades' Podium when
/// the game came from a lobby), and the host's pause menu as the general "end this early" from any game scene
/// (creative's only exit). Managers read <see cref="Active"/> and stand down while it's set; the HUDs read
/// <see cref="Active"/> + <see cref="Remaining"/> and swap their clock for the readout.
/// </summary>
[Title( "Lobby Return" )]
[Category( "Mimiclay" )]
[Icon( "logout" )]
public sealed class LobbyReturn : Component
{
	/// <summary>How long the readout shows before the scene changes.</summary>
	public const float Seconds = 5f;

	/// <summary>The readout's caption — the one string, so both HUDs say the same thing.</summary>
	public const string Caption = "Returning to Lobby";

	/// <summary>The live instance in this game scene (null in the lobby/menu, and in a scene that runs the
	/// throwaway <see cref="DebugGameMode"/> harness instead of a spawned manager).</summary>
	public static LobbyReturn Current { get; private set; }

	// ── Networked state (host writes, everyone reads) ────────────────────────────────────────────────────
	/// <summary>The countdown is running. Set LAST by <see cref="Begin"/>, after the timer, so a client never
	/// sees it armed with a stale clock.</summary>
	[Sync] public bool Returning { get; set; }

	/// <summary>When the scene changes (clock-skew-corrected per client).</summary>
	[Sync] public TimeUntil ReturnAt { get; set; }

	/// <summary>True on every machine while the return countdown runs — the game is frozen and the HUD shows
	/// the readout. Safe to read anywhere (false with no instance). Deliberately hides Component.Active — callers
	/// always mean this, and the component never needs its own.</summary>
	public static new bool Active => Current.IsValid() && Current.Returning;

	/// <summary>Seconds left on the readout (0 when not <see cref="Active"/>).</summary>
	public static float Remaining => Active ? MathF.Max( 0f, Current.ReturnAt ) : 0f;

	/// <summary>Fold into a HUD's BuildHash: changes when the countdown arms/disarms and as each second lands,
	/// so the readout re-renders without the parent having to know about the clock.</summary>
	public static int Signal => Active ? HashCode.Combine( true, (int)MathF.Ceiling( Remaining ) ) : 0;

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	bool _sceneChangeIssued; // host: ChangeScene fired — don't re-issue it on the frames before the scene unloads

	protected override void OnEnabled() => Current = this;

	protected override void OnDisabled()
	{
		if ( Current == this ) Current = null;
	}

	/// <summary>Host (or solo): arm the countdown. Idempotent — a second call while it's running is a no-op, so
	/// a manager's "phase over" and the pause menu's "end it now" can both call it freely. Returns false when
	/// there's no instance to arm (a debug scene) or we're a client; the caller falls back or does nothing.</summary>
	public static bool Begin( string reason = null )
	{
		if ( !Current.IsValid() )
			return false;

		if ( Networking.IsActive && !Networking.IsHost )
			return false;

		if ( Current.Returning )
			return true;

		Log.Info( $"LobbyReturn: returning to the lobby in {Seconds:0}s{(reason is null ? "" : $" — {reason}")}." );
		Current.ReturnAt = Seconds;
		Current.Returning = true;
		return true;
	}

	protected override void OnUpdate()
	{
		if ( !IsHostAuthority || !Returning || ReturnAt > 0f || _sceneChangeIssued )
			return;

		ChangeToLobby();
	}

	// Host-only. The spawner's SceneFile REFERENCE is the reliable path; the string lookup is only a fallback
	// for a map whose spawner hasn't wired LobbyScene (ResourceLibrary-by-path has returned null mid-session
	// before). A failed resolve pushes the timer back so this retries in a few seconds instead of once per
	// frame (the readout keeps showing — floored at "1" by Countdown — rather than flashing "0").
	void ChangeToLobby()
	{
		var options = new SceneLoadOptions();
		if ( !TryResolveLobby( options ) )
		{
			Log.Warning( "LobbyReturn: couldn't resolve the lobby scene — retrying in 5s. Wire LobbyScene on the map's RoundManagerSpawner." );
			ReturnAt = 5f;
			return;
		}

		_sceneChangeIssued = true;
		Game.ChangeScene( options );
	}

	/// <summary>Point <paramref name="options"/> at the lobby scene: the live spawner's reference first, the
	/// path lookup as the last resort. Shared with the pause menu's no-instance fallback.</summary>
	public static bool TryResolveLobby( SceneLoadOptions options )
	{
		var lobby = RoundManagerSpawner.Current.IsValid() ? RoundManagerSpawner.Current.LobbyScene : null;
		return lobby is not null ? options.SetScene( lobby ) : options.SetScene( LobbyController.LobbyScene );
	}
}
