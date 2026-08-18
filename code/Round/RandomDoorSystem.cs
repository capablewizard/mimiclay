using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Selects how many scene doors begin open using the round's synchronized seed. Only the host decides and
/// applies door state — <see cref="RoundDoor.SetOpen"/> is a <see cref="Rpc.Broadcast"/> call, so the resulting
/// rotation is explicitly replicated to every client rather than relying on each machine independently
/// re-deriving the same result from <see cref="RoundManager.DoorSeed"/>. In creative mode (no <see cref="RoundManager"/>,
/// no seed) every door is simply forced open instead, so builders always have full access to every room.
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

	bool IsHostAuthority => !Networking.IsActive || Networking.IsHost;

	int _appliedSeed;
	bool _appliedCreative;
	TimeSince _sinceResync;

	/// <summary>How often the host re-sends every door's current state regardless of change, so a player who
	/// joins mid-round (and therefore missed the original seed-triggered broadcasts) converges within this long.</summary>
	const float ResyncInterval = 2f;

	protected override void OnUpdate()
	{
		if ( !IsHostAuthority )
			return;

		// Creative maps never spawn a RoundManager, so there's no DoorSeed to react to — instead just force
		// every door open so builders always have full access to every room.
		if ( CreativeManager.Current.IsValid() )
		{
			if ( !_appliedCreative || _sinceResync >= ResyncInterval )
				OpenAll();

			return;
		}

		_appliedCreative = false;

		var manager = RoundManager.Current;
		if ( !manager.IsValid() || manager.DoorSeed == 0 )
			return;

		if ( _appliedSeed == manager.DoorSeed )
		{
			if ( _sinceResync >= ResyncInterval )
				Resync();

			return;
		}

		Apply( manager.DoorSeed );
		_appliedSeed = manager.DoorSeed;
		_sinceResync = 0;
	}

	/// <summary>Forces every door open, for creative mode. Re-run periodically (like <see cref="Resync"/>) so
	/// late joiners converge quickly.</summary>
	void OpenAll()
	{
		_appliedCreative = true;
		_sinceResync = 0;

		foreach ( var door in Scene.GetAllComponents<RoundDoor>() )
		{
			if ( !door.IsValid() )
				continue;

			door.SetOpen( true );
		}
	}

	/// <summary>Re-sends the already-decided state of every door, without re-rolling anything — catches up
	/// late joiners who missed the broadcasts from <see cref="Apply"/>.</summary>
	void Resync()
	{
		_sinceResync = 0;

		foreach ( var door in Scene.GetAllComponents<RoundDoor>() )
		{
			if ( !door.IsValid() )
				continue;

			door.SetOpen( door.IsOpen );
		}
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
