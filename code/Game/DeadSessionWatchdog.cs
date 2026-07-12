using System.Linq;
using Sandbox;

namespace Mimiclay;

/// <summary>
/// Detects a dead session in a gameplay scene — the host quit, crashed, or the connection dropped — and returns
/// the local player to the front-end menu with an explanation, instead of leaving them standing in a frozen round
/// forever (no phase driver, no way out but discovering the pause menu).
///
/// "Dead" = this process was deliberately in a session (<see cref="MenuNetworking.EverInSession"/>) but
/// <c>Networking.IsActive</c> has been false for a continuous grace window. The grace exists because a client
/// following the host's scene change is legitimately session-less for a moment while it reconnects — that state
/// resolves in seconds, while a dead session never comes back. This is a UX timeout, not a correctness gate (the
/// self-host forking that timing windows used to cause is prevented by the EverInSession check itself), so a
/// generous window is safe.
///
/// A GameObjectSystem, like <see cref="PauseMenuSystem"/>: exists in every scene with no wiring, skips the
/// front-end menu scene (nothing to watch there — and EverInSession is false once we've landed anyway).
/// </summary>
public sealed class DeadSessionWatchdog : GameObjectSystem
{
	const float GraceSeconds = 10f;

	RealTimeSince _sinceDead;
	bool _wasDead;

	public DeadSessionWatchdog( Scene scene ) : base( scene )
	{
		Listen( Stage.StartUpdate, 10, Tick, "DeadSessionWatchdog" );
	}

	void Tick()
	{
		if ( Scene is null )
			return;

		// Front-end menu scene → nothing to watch.
		if ( Scene.GetAllComponents<MainMenu>().Any() )
		{
			_wasDead = false;
			return;
		}

		var dead = MenuNetworking.EverInSession && !Networking.IsActive;
		if ( !dead )
		{
			_wasDead = false;
			return;
		}

		if ( !_wasDead )
		{
			_wasDead = true;
			_sinceDead = 0;
			return;
		}

		if ( _sinceDead < GraceSeconds )
			return;

		Log.Warning( $"DeadSessionWatchdog: no session for {GraceSeconds:0}s — the host is gone. Returning to the menu." );
		MenuNetworking.NotifyDisconnected( "Lost connection to the host." );
		MenuNetworking.ExitToMenu(); // clears EverInSession, so this fires once
	}
}
