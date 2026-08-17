using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Disables spawn points and other gameplay objects inside a room while its door(s) are closed, and re-enables
/// them once the room is accessible again. Drop one of these per room, point <see cref="Doors"/> at the
/// <see cref="RoundDoor"/>(s) that lead into it, and list whatever should be turned off in <see cref="Members"/>
/// (spawn points, loot, monster spawners, etc). Checked every tick so it reacts to whatever opens/closes the
/// doors — <see cref="RandomDoorSystem"/>, a lever, a keycard, whatever.
/// </summary>
[Title( "Room Controller" )]
[Category( "Mimiclay" )]
[Icon( "meeting_room" )]
[EditorHandle( "materials/gizmo/anchor.png" )]
public sealed class RoomController : Component
{
	/// <summary>The door(s) that lead into this room.</summary>
	[Property] public List<RoundDoor> Doors { get; set; } = new();

	/// <summary>If true (default), EVERY door must be open for the room to be considered accessible — closing
	/// any single door disables the room. If false, the room stays accessible as long as ANY one of its doors
	/// is open (only fully disabled once ALL doors are closed).</summary>
	[Property] public bool RequireAllDoorsOpen { get; set; } = true;

	/// <summary>Objects to enable/disable based on the room's accessibility (spawn points, spawners, loot, etc).</summary>
	[Property] public List<GameObject> Members { get; set; } = new();

	/// <summary>Other rooms this one is nested inside/behind. This room is only accessible if ALL of these are
	/// also accessible — e.g. a room inside another room should list the outer room here, so closing either
	/// room's doors disables both. Chains naturally: A depends on B depends on C all resolve correctly.</summary>
	[Property] public List<RoomController> DependsOn { get; set; } = new();

	/// <summary>Whether this room is currently reachable (its own door condition AND every room in
	/// <see cref="DependsOn"/> are satisfied).</summary>
	public bool IsAccessible { get; private set; } = true;

	bool? _appliedState;

	protected override void OnUpdate()
	{
		var doors = Doors.Where( d => d.IsValid() ).ToList();

		// Own doors: no doors configured -> this room's own condition is trivially satisfied.
		var ownAccessible = doors.Count == 0 || (RequireAllDoorsOpen
			? doors.All( d => d.IsOpen )
			: doors.Any( d => d.IsOpen ));

		var parentsAccessible = DependsOn
			.Where( r => r.IsValid() )
			.All( r => r.IsAccessible );

		IsAccessible = ownAccessible && parentsAccessible;

		if ( _appliedState == IsAccessible )
			return;

		Apply( IsAccessible );
		_appliedState = IsAccessible;
	}

	void Apply( bool accessible )
	{
		foreach ( var member in Members )
		{
			if ( !member.IsValid() )
				continue;

			member.Enabled = accessible;
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
	}
}
