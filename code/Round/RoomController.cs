using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>How a room's <see cref="RoomController.Doors"/> combine to decide accessibility.</summary>
public enum DoorRequirement
{
	/// <summary>Any ONE of the listed doors being open is enough — only disables once ALL are closed. Use this
	/// for a room with multiple separate entrances (either one gets you in).</summary>
	AnyOpen,

	/// <summary>EVERY listed door must be open — closing any single one disables the room. Use this when all
	/// the listed doors gate the SAME entrance (e.g. a door plus a vent cover over the same opening).</summary>
	AllOpen,
}

/// <summary>
/// Disables spawn points and other gameplay objects inside a room while its door(s) are closed, and re-enables
/// them once the room is accessible again. Drop one of these per room, point <see cref="Doors"/> at the
/// <see cref="RoundDoor"/>(s) that lead into it, and list whatever should be turned off in <see cref="Members"/>
/// (spawn points, loot, monster spawners, etc). Checked every tick so it reacts to whatever opens/closes the
/// doors — <see cref="RandomDoorSystem"/>, a lever, a keycard, whatever.
///
/// <see cref="GameObject.Enabled"/> is NOT a networked property, and door state (<see cref="RoundDoor.IsOpen"/>)
/// is only guaranteed deterministic across clients once every machine has applied the same synced
/// <c>RoundManager.DoorSeed</c> — relying on that timing for something as load-bearing as "is this spawn point
/// in play" is fragile (e.g. a late-joiner, or a frame where one client's <see cref="RandomDoorSystem"/> hasn't
/// applied yet, could disagree with everyone else). So only the host decides room accessibility, and the actual
/// <see cref="Members"/> enable/disable is replicated to everyone via <see cref="Rpc.Broadcast"/> — every machine
/// ends up with the exact same state the host computed, instead of independently re-deriving it.
/// </summary>
[Title( "Room Controller" )]
[Category( "Mimiclay" )]
[Icon( "meeting_room" )]
[EditorHandle( "materials/gizmo/anchor.png" )]
public sealed class RoomController : Component
{
	/// <summary>The door(s) that lead into this room.</summary>
	[Property] public List<RoundDoor> Doors { get; set; } = new();

	/// <summary>How <see cref="Doors"/> combine — <see cref="DoorRequirement.AllOpen"/> (default, matches this
	/// component's original behavior) for doors that all gate the same entrance, <see cref="DoorRequirement.AnyOpen"/>
	/// for a room with separate entrances. NOTE: this replaces the old <c>RequireAllDoorsOpen</c> bool — any
	/// room configured before this change needs its <see cref="Requirement"/> re-set in the inspector (the old
	/// serialized bool won't carry over automatically).</summary>
	[Property] public DoorRequirement Requirement { get; set; } = DoorRequirement.AllOpen;

	/// <summary>Objects to enable/disable based on the room's accessibility (spawn points, spawners, loot, etc).</summary>
	[Property] public List<GameObject> Members { get; set; } = new();

	/// <summary>Objects enabled only while this room is inaccessible, such as blockers, warning lights,
	/// or alternate spawn points. Do not include the same object in both member lists.</summary>
	[Property] public List<GameObject> InaccessibleMembers { get; set; } = new();

	/// <summary>Other rooms this one is nested inside/behind. This room is only accessible if ALL of these are
	/// also accessible — e.g. a room inside another room should list the outer room here, so closing either
	/// room's doors disables both. Chains naturally: A depends on B depends on C all resolve correctly.</summary>
	[Property] public List<RoomController> DependsOn { get; set; } = new();

	/// <summary>Whether this room is currently reachable. Host-computed, replicated to every machine via
	/// <see cref="BroadcastAccessibility"/> — read this instead of recomputing it locally.</summary>
	public bool IsAccessible { get; private set; } = true;

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	bool? _appliedState;
	TimeSince _sinceResync;	

	/// <summary>How often the host re-broadcasts current state regardless of change, so a player who joins
	/// mid-round (and therefore missed the original change-triggered broadcast) converges within this long.</summary>
	const float ResyncInterval = 2f;

	protected override void OnUpdate()
	{
		if ( !IsHostAuthority )
			return;

		var accessible = ComputeAccessible( new HashSet<RoomController>() );

		if ( _appliedState == accessible && _sinceResync < ResyncInterval )
			return;

		_appliedState = accessible;
		_sinceResync = 0;
		BroadcastAccessibility( accessible );
	}

	/// <summary>Recursively evaluates own door condition AND every <see cref="DependsOn"/> room, rather than
	/// reading their cached <see cref="IsAccessible"/> — avoids depending on component update order between
	/// different GameObjects on the host, since this all runs synchronously in one call.</summary>
	bool ComputeAccessible( HashSet<RoomController> visited )
	{
		if ( !visited.Add( this ) )
			return true; // cycle guard — a room can't gate itself

		var doors = Doors.Where( d => d.IsValid() ).ToList();

		var ownAccessible = doors.Count == 0 || (Requirement == DoorRequirement.AllOpen
			? doors.All( d => d.IsOpen )
			: doors.Any( d => d.IsOpen ));

		if ( !ownAccessible )
			return false;

		return DependsOn
			.Where( r => r.IsValid() )
			.All( r => r.ComputeAccessible( visited ) );
	}

	/// <summary>Host decides, then this replicates the exact result (and applies it) on every machine —
	/// including the host itself, since <see cref="Rpc.Broadcast"/> runs locally too.</summary>
	[Rpc.Broadcast]
	void BroadcastAccessibility( bool accessible )
	{
		IsAccessible = accessible;

		foreach ( var member in Members )
		{
			if ( member.IsValid() )
				member.Enabled = accessible;
		}

		foreach ( var member in InaccessibleMembers )
		{
			if ( member.IsValid() )
				member.Enabled = !accessible;
		}
	}

	protected override void DrawGizmos()
	{
		base.DrawGizmos();

		if ( !Gizmo.IsSelected && !Gizmo.IsHovered )
			return;

		Gizmo.Transform = new Transform( 0 );

		Gizmo.Draw.Color = IsAccessible ? Color.Green.WithAlpha( 0.6f ) : Color.Red.WithAlpha( 0.6f );

		foreach ( var door in Doors )
		{
			if ( !door.IsValid() )
				continue;

			Gizmo.Draw.Line( WorldPosition, door.WorldPosition );
		}

		foreach ( var member in Members )
		{
			if ( !member.IsValid() )
				continue;

			Gizmo.Draw.Line( WorldPosition, member.WorldPosition );
		}

		Gizmo.Draw.Color = Color.Yellow.WithAlpha( 0.6f );

		foreach ( var parent in DependsOn )
		{
			if ( !parent.IsValid() )
				continue;

			Gizmo.Draw.Line( WorldPosition, parent.WorldPosition );
		}

		Gizmo.Draw.Color = Color.Orange.WithAlpha( 0.6f );

		foreach ( var member in InaccessibleMembers )
		{
			if ( member.IsValid() )
				Gizmo.Draw.Line( WorldPosition, member.WorldPosition );
		}

		Gizmo.Draw.Color = Color.Yellow.WithAlpha( 0.6f );
	}
}
