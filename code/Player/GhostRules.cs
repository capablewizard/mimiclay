using System;

namespace Mimiclay;

/// <summary>Which side a ghost — or a viewer — is on, for <see cref="GhostRules"/>.</summary>
public enum GhostTeam
{
	/// <summary>No side: waiting at the load gate, joined too late for a role, or no round at all.</summary>
	None,
	Props,
	Hunters,
}

/// <summary>
/// Who sees free-cam ghosts, in one place — asked by the ghost (<see cref="SpectatorGhost"/>: drawn or not, its glow),
/// its name tag (<see cref="PlayerNameplates"/>) and the free cam (<see cref="SpectatorController"/>: make one at all,
/// put it on the network yet). Everything comes from round state every machine already has: the roster's roles, the
/// phase and the round's "Show ghosts" option (<see cref="RoundSettings.Ghosts"/>).
/// <list type="bullet">
/// <item><b>Teams</b> come from the roster, live: a prop (living, scouting, or eliminated in Teams) is Props, a hunter is
/// Hunters, no role is None. An Infection prop who gets caught is Hunters the moment the roster says so.</item>
/// <item>You always see your own ghost (from your third person), whatever the option.</item>
/// <item><b>None</b>: nobody sees anyone else's ghost. <b>Team</b>: props see props; hunters and the roleless never see
/// ghosts. <b>All</b>: everyone. A living prop's ghost shows to the other team — and in Infection to everyone — only
/// once it has been out a few seconds (<see cref="SpawnDelayed"/>), so it can leave its body before it's seen.</item>
/// <item><b>Reveal</b> onward the round's secrets are out: every ghost shows to everyone, names included.</item>
/// <item>Outside a prop-hunt round (creative, the lobby) there's nothing to hide: everyone sees everything.</item>
/// </list>
/// </summary>
public static class GhostRules
{
	static RoundManager Round => RoundManager.Current;

	static bool InRound => Round.IsValid();

	/// <summary>The round has reached the Reveal (or the gallery after it): no secrets left, every ghost shows to
	/// everyone.</summary>
	public static bool Revealing => InRound && Round.Phase is RoundPhase.Reveal or RoundPhase.Consolidation;

	/// <summary>This machine's player's side.</summary>
	public static GhostTeam LocalTeam => TeamOf( RoundManager.LocalRole );

	/// <summary>The side of the player behind <paramref name="connection"/> (a ghost's owner), from the roster.</summary>
	public static GhostTeam TeamOf( Guid? connection )
		=> InRound && connection is { } id && Round.Players.TryGetValue( id, out var row ) ? TeamOf( row.Role ) : GhostTeam.None;

	static GhostTeam TeamOf( PlayerRole role ) => role switch
	{
		PlayerRole.Prop => GhostTeam.Props,
		PlayerRole.Hunter => GhostTeam.Hunters,
		_ => GhostTeam.None,
	};

	/// <summary>Is the player behind <paramref name="connection"/> out of the round (a found Teams prop)? Their ghost has
	/// no body to give away, so it skips the spawn delay.</summary>
	public static bool Eliminated( Guid? connection )
		=> InRound && connection is { } id && Round.Players.TryGetValue( id, out var row ) && Round.IsEliminated( row );

	/// <summary>Is a ghost on <paramref name="owner"/>'s side our own team? Own-team ghosts show at once and wear names.</summary>
	public static bool SameTeam( GhostTeam owner ) => owner != GhostTeam.None && owner == LocalTeam;

	/// <summary>May this machine's player see someone else's ghost on <paramref name="owner"/>'s side at all? (The
	/// spawn delay comes on top — <see cref="SpawnDelayed"/>.)</summary>
	public static bool MaySee( GhostTeam owner )
	{
		if ( !InRound || Revealing )
			return true;

		return Round.Settings.Ghosts switch
		{
			GhostVisibility.Team => LocalTeam == GhostTeam.Props && owner == GhostTeam.Props,
			GhostVisibility.All => true,
			_ => false,
		};
	}

	/// <summary>May a ghost on <paramref name="owner"/>'s side wear its name here? For its own team, and always for
	/// hunters — whoever it is shows in the ghost's face anyway, and which prop is scouting tells them little.</summary>
	public static bool MayShowName( GhostTeam owner ) => !InRound || Revealing || SameTeam( owner ) || LocalTeam == GhostTeam.Hunters;

	/// <summary>Does a ghost on <paramref name="owner"/>'s side, owned by <paramref name="connection"/>, wait out the
	/// spawn delay before showing here (<see cref="SpectatorGhost.SpawnHideSeconds"/>)? Only a living prop's ghost —
	/// it appears where its body hides — and only to a viewer who could use that: the other team, or in Infection
	/// anyone (a teammate can be infected and carry it across). Never once the round is revealed.</summary>
	public static bool SpawnDelayed( GhostTeam owner, Guid? connection )
		=> InRound && !Revealing && owner == GhostTeam.Props && !Eliminated( connection )
		&& (!SameTeam( owner ) || Round.Settings.Mode == RoundMode.Infection);

	/// <summary>Could anyone else see a ghost right now? Not under None until the Reveal — then a ghost stays local
	/// (its owner still sees it), so nothing is sent over the network for no one.</summary>
	public static bool GhostShared => !InRound || Revealing || Round.Settings.Ghosts != GhostVisibility.None;

	/// <summary>May a ghost go on the network yet? Never before its owner's body does: an Infection prop stays off the
	/// network through Hide, and a networked ghost beside it would hand every machine (a modified client included)
	/// where it is. Until then the ghost is local — its owner still sees it in third person.</summary>
	public static bool MayNetwork( GameObject leftBehind )
		=> Networking.IsActive && (!leftBehind.IsValid() || leftBehind.Network.Active);
}
