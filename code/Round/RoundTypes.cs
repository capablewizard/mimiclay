using System;

namespace Mimiclay;

/// <summary>
/// The phases of a prop-hunt session, in loop order. <see cref="Lobby"/> lives in its own scene
/// (<c>lobby.scene</c>, driven by <see cref="LobbyController"/>); every other phase plays out inside the chosen
/// map scene and is driven by <see cref="RoundManager"/>. The HUD reads the active phase through
/// <see cref="IRoundContext"/> so it doesn't care which manager is running.
///
/// Flow: <see cref="Lobby"/> → host picks map + starts → load map scene → <see cref="Starting"/> (countdown,
/// pawns spawned but frozen) → <see cref="Hide"/> (props build disguises, hunters study; players can't see each
/// other) → <see cref="Hunt"/> (hunters returned to spawn, shoot props on a cooldown) → <see cref="Reveal"/>
/// (surviving props flash) → <see cref="Consolidation"/> (disguise gallery + stats) → back to <see cref="Lobby"/>.
/// </summary>
public enum RoundPhase
{
	/// <summary>Pre-game hub in its own scene: edit appearance, nominate as hunter, host configures + starts.</summary>
	Lobby,
	/// <summary>Brief "get ready" countdown after the map loads; pawns exist but input is frozen.</summary>
	Starting,
	/// <summary>Props sculpt their disguise, hunters study the map. No player can see another player.</summary>
	Hide,
	/// <summary>Hunters are returned to spawn and hunt the props for the search time.</summary>
	Hunt,
	/// <summary>Search time elapsed with survivors: the remaining props are flashed on the map.</summary>
	Reveal,
	/// <summary>Disguise gallery (thumbs up/down) + hunter stats before returning to the lobby.</summary>
	Consolidation,
}

/// <summary>Which side a player is on for the current round. <see cref="Unassigned"/> is the lobby default
/// (a player who's only been editing/idling). Roles are decided at round start from hunter nominations.</summary>
public enum PlayerRole
{
	Unassigned,
	Hunter,
	Prop,
}

/// <summary>
/// Per-player round state, replicated to everyone via the manager's <c>NetDictionary&lt;Guid, PlayerInfo&gt;</c>
/// keyed by <see cref="Connection.Id"/>. It's a plain struct of primitives so s&amp;box serialises it over the
/// network with no extra attributes (see the NetDictionary value-serialisation contract). It outlives the player's
/// pawn — pawns are destroyed/respawned across phases, but this row persists for the whole round (and is what the
/// scoreboard, consolidation screen and win-check all read).
/// </summary>
public struct PlayerInfo
{
	/// <summary>The owning connection (dictionary key, duplicated here so values are self-describing).</summary>
	public Guid Connection;

	/// <summary>Display name, snapshotted at join so the row survives a disconnect during consolidation.</summary>
	public string Name;

	/// <summary>Hunter or Prop for this round (decided at <see cref="RoundPhase.Starting"/>).</summary>
	public PlayerRole Role;

	/// <summary>A prop that hasn't been found yet. Cleared when a prop is shot — and NOT reset by the
	/// conversion, so a converted player's row reads Role=Hunter with Alive=false. Alive is only meaningful
	/// for props (the win-check counts Role==Prop &amp;&amp; Alive); never gate hunters on it.</summary>
	public bool Alive;

	/// <summary>This prop was found by a hunter (drives stats + "converted to hunter").</summary>
	public bool Found;

	/// <summary>True if this player asked to be a hunter in the lobby (nomination). Read at role assignment.</summary>
	public bool Nominated;

	/// <summary>Round score. Props accrue points over time scaled by size; hunters earn points per find.</summary>
	public int Score;

	/// <summary>Which spawn point of this player's kind they use, assigned by the host so each player picks a distinct
	/// spot when they spawn their OWN pawn (the host no longer spawns pawns — see <see cref="RoundManager"/>).</summary>
	public int SpawnIndex;

	/// <summary>This row is a TEST BOT, not a real connection: no machine, no input, nobody behind it. It plays the
	/// game everywhere the roster is the truth (counts, win checks, scoring, the HUD tally) and the HOST spawns its
	/// body, because there's no owning machine to spawn one. Two places have to know: the leaver sweep
	/// (<see cref="RoundManager"/>.ReconcileConnections) must not drop a row with no connection behind it, and
	/// pawn ownership must be resolved through <see cref="RoundManager.RosterIdOf"/> rather than Network.Owner.</summary>
	public bool Bot;

	/// <summary>The GameObject id of this player's NETWORKED pawn, stamped by the HOST as it observes its own
	/// scene (the host misses no create/destroy — every message routes through it). <see cref="Guid.Empty"/>
	/// while the pawn is unpublished (infection prep, the publish gate) or gone. This is the roster's answer to
	/// "which pawn object should exist on my machine": the engine silently DROPS object create/destroy messages
	/// that arrive while a machine is mid-scene-load, so without this a machine can be permanently missing a
	/// pawn — or keeping a ghost of a destroyed one — with nothing ever noticing. Reconciled per machine by
	/// <see cref="RoundManager"/>'s pawn-presence heal.</summary>
	public Guid PawnId;
}

/// <summary>Debug override for the side the LOCAL player takes at role assignment, so you can test either seat
/// without restarting until the dice agree. <see cref="Auto"/> is normal play (lobby nominations, then chance).
/// Set on the map's <see cref="RoundManagerSpawner"/>.</summary>
public enum PlayAsChoice
{
	/// <summary>Normal: the lobby's nominations decide, falling back to a random draw.</summary>
	Auto,
	Hunter,
	Prop,
}
