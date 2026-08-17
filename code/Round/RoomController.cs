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

	/// <summary>If true, EVERY door must be open for the room to be considered accessible. If false (default),
	/// the room is accessible as soon as ANY one of its doors is open.</summary>
	[Property] public bool RequireAllDoorsOpen { get; set; }

	/// <summary>Objects to enable/disable based on the room's accessibility (spawn points, spawners, loot, etc).</summary>
	[Property] public List<GameObject> Members { get; set; } = new();

	/// <summary>Whether this room is currently reachable (its door condition is satisfied).</summary>
	public bool IsAccessible { get; private set; } = true;

	bool? _appliedState;

	protected override void OnUpdate()
	{
		var doors = Doors.Where( d => d.IsValid() ).ToList();

		// No doors configured -> nothing gates this room, leave members as authored.
		if ( doors.Count == 0 )
			return;

		IsAccessible = RequireAllDoorsOpen
			? doors.All( d => d.IsOpen )
			: doors.Any( d => d.IsOpen );

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
	}
}
