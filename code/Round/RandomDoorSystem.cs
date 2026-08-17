using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Selects how many scene doors begin open using the round's synchronized seed.
/// </summary>
[Title( "Random Door System" )]
[Category( "Mimiclay" )]
[Icon( "meeting_room" )]
public sealed class RandomDoorSystem : Component
{
	[Property, Range( 0, 128 )]
	public int MinimumOpen { get; set; } = 2;

	[Property, Range( 0, 128 )]
	public int MaximumOpen { get; set; } = 4;

	int _appliedSeed;

	protected override void OnUpdate()
	{
		var manager = RoundManager.Current;
		if ( !manager.IsValid() || manager.DoorSeed == 0 )
			return;

		if ( _appliedSeed == manager.DoorSeed )
			return;

		Apply( manager.DoorSeed );
		_appliedSeed = manager.DoorSeed;
	}

	void Apply( int seed )
	{
		var doors = Scene.GetAllComponents<RoundDoor>()
			.Where( door => door.IsValid() )
			.OrderBy( door => door.GameObject.Id )
			.ToList();

		if ( doors.Count == 0 )
		{
			Log.Warning( "RandomDoorSystem: no RoundDoor components found." );
			return;
		}

		var minimum = Math.Clamp( MinimumOpen, 0, doors.Count );
		var maximum = Math.Clamp( MaximumOpen, minimum, doors.Count );
		var random = new System.Random( seed );
		var openCount = random.Next( minimum, maximum + 1 );

		foreach ( var door in doors )
			door.SetOpen( false );

		Shuffle( doors, random );

		for ( var i = 0; i < openCount; i++ )
			doors[i].SetOpen( true );

		Log.Info( $"RandomDoorSystem: opened {openCount} of {doors.Count} doors." );
	}

	static void Shuffle( List<RoundDoor> doors, System.Random random )
	{
		for ( var i = doors.Count - 1; i > 0; i-- )
		{
			var other = random.Next( i + 1 );
			(doors[i], doors[other]) = (doors[other], doors[i]);
		}
	}
}
