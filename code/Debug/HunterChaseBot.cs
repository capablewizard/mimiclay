using System;
using System.Threading.Tasks;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// A scripted hunter (user, 2026-11-06: "a bot that chases me around and throws me… a real player controlled by a
/// script, activated by a console command while you're a hunter, so I can see it from different perspectives").
/// Run <c>mimi_bot_chase</c> on the machine whose hunter should be driven: it chases the nearest other player's prop,
/// grabs it, carries it to a random spot, then drops or throws it, rests, and goes again — through the same grab, hold and
/// release the player would use (PropClaims.RequestGrab, the hold driver, HunterController.LetGo), moved by the hunter's
/// debug chase override (HunterController.DebugChase). <c>mimi_bot_chase</c> again stops it.
/// </summary>
public static class HunterChaseBot
{
	public static bool Active { get; private set; }

	[ConVar( "mimi_bot_throw_chance", Help = "Chase bot: how often a carry ends in a throw rather than a drop (0..1)." )]
	public static float ThrowChance { get; set; } = 0.5f;
	[ConVar( "mimi_bot_rest", Help = "Chase bot: seconds it waits after letting go before chasing again." )]
	public static float Rest { get; set; } = 4f;
	[ConVar( "mimi_bot_carry", Help = "Chase bot: seconds it carries you before letting go." )]
	public static float Carry { get; set; } = 3f;

	static int _run; // generation: a stop (or a restart) invalidates the running loop

	[ConCmd( "mimi_bot_chase", Help = "Toggle the chase bot on THIS machine's hunter: it hunts the nearest other player's prop (or only the named player's: mimi_bot_chase Lisa), grabs it, carries it, drops or throws it, and repeats." )]
	public static void Toggle( string onlyPlayer = "" )
	{
		if ( Active )
		{
			Stop( "stopped" );
			return;
		}
		var me = Game.ActiveScene?.GetAllComponents<HunterController>().FirstOrDefault( h => !h.IsProxy );
		if ( me is null )
		{
			Log.Info( "mimi_bot_chase: you need to be a HUNTER on this machine." );
			return;
		}
		Active = true;
		OnlyPlayer = onlyPlayer ?? "";
		Log.Info( $"mimi_bot_chase: ON — hunting {(OnlyPlayer.Length > 0 ? $"'{OnlyPlayer}'" : "the nearest other player")}'s prop. Run again to stop." );
		_ = RunAsync( ++_run );
	}

	/// <summary>Only this player's prop is hunted (display name, case-insensitive); empty = whoever is nearest.</summary>
	public static string OnlyPlayer { get; set; } = "";
	static HiderController _holding; // the prop in our hands right now, let go of on Stop

	static void Stop( string why )
	{
		Active = false;
		_run++;
		HunterController.DebugChase = null;
		HunterController.DebugGrabHold = false;
		var me = Game.ActiveScene?.GetAllComponents<HunterController>().FirstOrDefault( h => !h.IsProxy );
		if ( _holding.IsValid() && me.IsValid() )
			me.LetGo( _holding.GameObject, false ); // never leave someone hanging in a stopped bot's hands
		_holding = null;
		Log.Info( $"mimi_bot_chase: {why}." );
	}

	static HiderController NearestVictim( Vector3 from )
		=> Game.ActiveScene.GetAllComponents<HiderController>()
			.Where( h => h.IsProxy && h.Network.Owner is not null && !RoundManager.IsBotPawn( h.GameObject )
				&& !PropClaims.IsReleased( h ) && !PropClaims.IsHeldObject( h.GameObject )
				&& (OnlyPlayer.Length == 0 || string.Equals( h.Network.Owner.DisplayName, OnlyPlayer, StringComparison.OrdinalIgnoreCase )) )
			.OrderBy( h => h.WorldPosition.Distance( from ) )
			.FirstOrDefault();

	static async Task RunAsync( int run )
	{
		var rnd = new Random();
		int rounds = 0;
		while ( Active && run == _run )
		{
			var scene = Game.ActiveScene;
			var me = scene?.GetAllComponents<HunterController>().FirstOrDefault( h => !h.IsProxy );
			if ( me is null )
			{
				Stop( "no hunter on this machine any more" );
				return;
			}
			var pc = me.Components.Get<PlayerController>();
			var target = NearestVictim( me.WorldPosition );
			if ( target is null )
			{
				HunterController.DebugChase = null;
				await GameTask.Delay( 500 );
				continue;
			}

			// Chase, up to 15 s.
			float t0 = Time.Now;
			bool near = false;
			var lastPos = me.WorldPosition;
			float stuckSince = Time.Now;
			while ( Active && run == _run && Time.Now - t0 < 15f )
			{
				if ( !target.IsValid() )
					break;
				// Wedged on scenery (measured: stuck on a traffic cone with the wish pointing into it)? Step sideways for a moment,
				// as a player would, then carry on.
				if ( me.WorldPosition.Distance( lastPos ) > 10f )
				{
					lastPos = me.WorldPosition;
					stuckSince = Time.Now;
				}
				else if ( Time.Now - stuckSince > 0.7f )
				{
					var toTarget = (target.WorldPosition - me.WorldPosition).WithZ( 0f ).Normal;
					var side = rnd.Next( 2 ) == 0 ? toTarget.Cross( Vector3.Up ) : Vector3.Up.Cross( toTarget );
					HunterController.DebugChase = me.WorldPosition + side * 120f - toTarget * 40f;
					await GameTask.Delay( 600 );
					lastPos = me.WorldPosition;
					stuckSince = Time.Now;
					continue;
				}
				HunterController.DebugChase = target.WorldPosition;
				if ( me.WorldPosition.WithZ( 0f ).Distance( target.WorldPosition.WithZ( 0f ) ) < 110f )
				{
					near = true;
					break;
				}
				await GameTask.Delay( 50 );
			}
			if ( !near || !target.IsValid() || !Active || run != _run )
			{
				HunterController.DebugChase = null;
				await GameTask.Delay( 1000 );
				continue;
			}

			// Grab (the real request; the host decides).
			HunterController.DebugChase = target.WorldPosition;
			HunterController.DebugGrabHold = true;
			PropClaims.Current?.RequestGrab( target.GameObject );
			await GameTask.Delay( 900 );
			if ( !Active || run != _run )
				return;
			if ( !PropClaims.IsHeldObject( target.GameObject ) )
			{
				HunterController.DebugGrabHold = false;
				HunterController.DebugChase = null;
				await GameTask.Delay( 1500 );
				continue;
			}
			rounds++;
			_holding = target;

			// Carry it somewhere 150–300u away.
			float ang = (float)(rnd.NextDouble() * MathF.PI * 2);
			float dist = 150f + (float)rnd.NextDouble() * 150f;
			HunterController.DebugChase = me.WorldPosition + new Vector3( MathF.Cos( ang ) * dist, MathF.Sin( ang ) * dist, 0f );
			await GameTask.Delay( (int)(Carry * 1000f) );
			if ( !Active || run != _run )
				return;
			HunterController.DebugChase = me.WorldPosition; // stand
			await GameTask.Delay( 300 );

			// Let go: a throw (a swing of the view) or a plain drop.
			bool throwIt = rnd.NextDouble() < ThrowChance;
			if ( throwIt && pc.IsValid() )
			{
				var a0 = pc.EyeAngles;
				for ( int i = 0; i < 12; i++ )
				{
					await GameTask.Delay( 16 );
					var a = a0;
					a.yaw += (i + 1) * 4f;
					a.pitch -= (i + 1) * 2.5f;
					pc.EyeAngles = a;
				}
			}
			HunterController.DebugGrabHold = false;
			if ( target.IsValid() && me.IsValid() )
				me.LetGo( target.GameObject, false );
			_holding = null;
			Log.Info( $"mimi_bot_chase: round {rounds} — {(throwIt ? "threw" : "dropped")} '{target.GameObject.Name}'." );
			HunterController.DebugChase = null;
			await GameTask.Delay( (int)(Rest * 1000f) + rnd.Next( 1500 ) );
		}
	}
}
