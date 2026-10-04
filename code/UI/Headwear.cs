using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// What a player wears on their roster icon: the host's 👑 crown, a party hat, or nothing. ONE rule for every
/// screen that shows player icons (RoundHud's lobby and hunter pips, the charades scoreboard and podium), so
/// they can't disagree.
///
///   • The host always wears the crown, party or not. The crown wins: a host who's also in the party doesn't
///     get a hat as well.
///   • A party hat marks players who came together: anyone whose Steam party (<see cref="Connection.PartyId"/>)
///     has at least one other member in this game. A "party" of one is just a player, so no hat.
///   • ...but only while the game is MIXED. When everyone in the game shares one party, a hat on every head says
///     nothing, so nobody wears one (the host still has the crown).
///   • Each party's hats share a colour (<see cref="HatHue"/>), so separate parties in one game read as separate
///     groups.
///
/// PartyId is engine connection info, networked to every machine, so all players see the same hats in the same
/// colours. Bot rows have no connection, so they're never in a party, and they don't count towards "everyone"
/// either: a party playing only with bots counts as everyone being in the party.
/// </summary>
public static class Headwear
{
	public enum Kind { None, Crown, PartyHat }

	[ConVar( "mimi_dbg_party", Help = "Fake parties: every bot row (no real connection) wears a party hat, as if the bots came in two parties (alternating seats) in a mixed game. For checking hats without a Steam party." )]
	public static bool DebugFakeParty { get; set; }

	/// <summary>
	/// Hue rotations for each party's hats, in the order parties get them. Hand-picked rather than evenly spaced:
	/// the hat's blue/yellow/brown goes muddy at 180° and garish at 240°. Slot 0 is the hat as drawn. Beyond four
	/// parties the colours repeat, which a lobby that size will rarely hit.
	/// </summary>
	static readonly float[] HueSlots = { 0f, 120f, 300f, 60f };

	/// <summary>What this roster id wears right now.</summary>
	public static Kind Of( Guid connection )
	{
		// The Empty guard is on the crown only: HostId reads Empty when there's no host to find, and that mustn't
		// crown an empty id. Empty IS a real roster id, though: RoundBots.IdFor( 0 ) builds the all-zero guid, so
		// it's "Bot 1".
		if ( connection != Guid.Empty && connection == HostId ) return Kind.Crown;
		return SlotOf( connection ) is not null ? Kind.PartyHat : Kind.None;
	}

	/// <summary>
	/// Hue rotation in degrees for this player's party hat: the same for everyone in one party. 0 for anyone not
	/// wearing a hat. Applied as an inline <c>filter-hue-rotate</c> on the hat image; the drop-shadow outline is
	/// drawn before the colour filters, so it stays ink-dark.
	/// </summary>
	public static float HatHue( Guid connection )
		=> SlotOf( connection ) is { } slot ? HueSlots[slot % HueSlots.Length] : 0f;

	/// <summary>
	/// The session host's roster id. Offline the local player is the host. Bot rows carry made-up ids, so they
	/// never match.
	/// </summary>
	public static Guid HostId => Networking.IsActive
		? Connection.All.FirstOrDefault( c => c.IsHost )?.Id ?? Guid.Empty
		: Connection.Local?.Id ?? Guid.Empty;

	/// <summary>
	/// Hash of who wears what, in which colour. Fold it into BuildHash so the icons re-render when someone joins,
	/// leaves, changes party or takes over as host. Nothing else in a HUD's hash would notice a party change.
	/// </summary>
	public static int Signal
	{
		get
		{
			var hash = HashCode.Combine( HostId, Slots.Count, DebugFakeParty );
			foreach ( var (id, slot) in Slots )
				hash ^= HashCode.Combine( id, slot );
			return hash;
		}
	}

	// ── Who wears a hat, in which colour ────────────────────────────────────────────────────────────────────
	// Roster id → the party's palette slot. Every screen asks for every row on each rebuild, so the map is worked
	// out once per frame, not per row.
	static readonly Dictionary<Guid, int> _slots = new();
	static float _builtAt = float.NaN;

	static Dictionary<Guid, int> Slots
	{
		get
		{
			if ( _builtAt != Time.Now )
			{
				_builtAt = Time.Now;
				Rebuild();
			}
			return _slots;
		}
	}

	static int? SlotOf( Guid connection )
	{
		if ( Slots.TryGetValue( connection, out var slot ) ) return slot;

		// mimi_dbg_party: bot ids aren't connections, so they never reach the real map; fake them in here, two
		// parties by alternating seat (the seat number is the bot id's last field, see RoundBots.IdFor).
		if ( DebugFakeParty && Connection.Find( connection ) is null )
			return long.TryParse( connection.ToString( "D" )[^12..], out var seat ) ? (int)(seat % 2) : 0;

		return null;
	}

	static void Rebuild()
	{
		_slots.Clear();
		if ( !Networking.IsActive ) return;

		var players = Connection.All.Where( c => c is not null ).ToList();

		// How many players each party has here. A PartyId of 0 means "not in a party".
		var sizes = new Dictionary<ulong, int>();
		foreach ( var c in players )
		{
			ulong party = c.PartyId;
			if ( party != 0 )
				sizes[party] = sizes.GetValueOrDefault( party ) + 1;
		}

		// Everyone came as one party: hats would be on every head and mean nothing.
		if ( sizes.Count == 1 && sizes.Values.First() == players.Count ) return;

		// Real parties (2+ here) get palette slots in PartyId order: every machine sees the same ids, so every
		// machine hands out the same colours, and a party keeps its colour while others come and go around it
		// unless one with a lower id joins.
		var slotOfParty = sizes.Where( kv => kv.Value >= 2 )
			.Select( kv => kv.Key )
			.OrderBy( id => id )
			.Select( ( id, i ) => (id, i) )
			.ToDictionary( x => x.id, x => x.i );

		foreach ( var c in players )
		{
			if ( slotOfParty.TryGetValue( c.PartyId, out var slot ) )
				_slots[c.Id] = slot;
		}
	}
}
