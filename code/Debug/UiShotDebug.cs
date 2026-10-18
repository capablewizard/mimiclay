using System;
using System.Linq;
using Sandbox.UI.Navigation;

namespace Mimiclay;

/// <summary>
/// Console shortcuts that put the game into each UI state without a keyboard, so tooling can screenshot every
/// screen (pause menu, edit HUD, each round / charades phase, the menu-scene pages, the tutorial). Debug only —
/// nothing here is reachable from play, and every command is a no-op when its state doesn't apply.
/// </summary>
public static class UiShotDebug
{
	/// <summary>Toggle the in-game pause menu (what Escape does).</summary>
	[ConCmd( "mimi_shot_pause" )]
	static void Pause()
	{
		var menu = PauseMenu.Current;
		if ( !menu.IsValid() ) { Log.Warning( "mimi_shot_pause: no pause menu in this scene." ); return; }
		menu.SetOpen( !PauseMenu.IsOpen );
	}

	/// <summary>Toggle face-edit on the local hunter (what Q does).</summary>
	[ConCmd( "mimi_shot_edit" )]
	static void Edit()
	{
		var meH = Connection.Local?.Id;
		var hider = Game.ActiveScene.GetAllComponents<HiderController>()
			.FirstOrDefault( h => !h.IsProxy && RoundManager.RosterIdOf( h.GameObject ) == meH );
		if ( hider.IsValid() ) { if ( hider.EditMode ) hider.ExitEditing(); else hider.EnterEditing(); return; }
		var me = Connection.Local?.Id;
		var hunter = Game.ActiveScene.GetAllComponents<HunterController>()
			.FirstOrDefault( h => h.Owned && RoundManager.RosterIdOf( h.GameObject ) == me );
		if ( !hunter.IsValid() ) { Log.Warning( "mimi_shot_edit: no owned hunter pawn." ); return; }
		hunter.ToggleEdit();
	}

	/// <summary>Host: jump the prop-hunt round to a phase (starting, hide, hunt, reveal, consolidation).</summary>
	[ConCmd( "mimi_shot_phase" )]
	static void Phase( string phase )
	{
		var rm = RoundManager.Current;
		if ( !rm.IsValid() ) { Log.Warning( "mimi_shot_phase: no RoundManager." ); return; }
		if ( !Enum.TryParse<RoundPhase>( phase, true, out var p ) ) { Log.Warning( $"mimi_shot_phase: unknown phase '{phase}'." ); return; }
		rm.TransitionTo( p );
	}

	/// <summary>Host: jump charades to a phase (waiting, starting, writing, choosing, sculpting, turnreveal, podium).</summary>
	[ConCmd( "mimi_shot_charades" )]
	static void Charades( string phase )
	{
		var cm = CharadesManager.Current;
		if ( !cm.IsValid() ) { Log.Warning( "mimi_shot_charades: no CharadesManager." ); return; }
		if ( !Enum.TryParse<CharadesPhase>( phase, true, out var p ) ) { Log.Warning( $"mimi_shot_charades: unknown phase '{phase}'." ); return; }
		cm.TransitionTo( p );
	}

	/// <summary>Menu scene: navigate the main menu to a route ("/", "/customise", "/library", "/options").</summary>
	[ConCmd( "mimi_shot_menu" )]
	static void Menu( string route )
	{
		var nav = MainMenuNav.Instance;
		if ( nav is null ) { Log.Warning( "mimi_shot_menu: no MainMenuNav (open menu.scene)." ); return; }
		nav.Navigate( route );
	}

	/// <summary>Start the guided tutorial on the local hunter (what interacting with the tutorial NPC does).</summary>
	[ConCmd( "mimi_shot_tutorial" )]
	static void Tutorial()
	{
		var npc = Game.ActiveScene.GetAllComponents<TutorialNpc>().FirstOrDefault();
		var me = Connection.Local?.Id;
		var hunter = Game.ActiveScene.GetAllComponents<HunterController>()
			.FirstOrDefault( h => h.Owned && RoundManager.RosterIdOf( h.GameObject ) == me );
		if ( !npc.IsValid() || !hunter.IsValid() ) { Log.Warning( "mimi_shot_tutorial: need a TutorialNpc and an owned hunter." ); return; }
		npc.BeginTutorial( hunter );
	}

	/// <summary>Arm the shared "returning to lobby" countdown readout.</summary>
	[ConCmd( "mimi_shot_return" )]
	static void Return() => LobbyReturn.Begin( "screenshot" );

	/// <summary>Lobby: seat N test bots (and nominate the first M) on the live LobbyController.</summary>
	[ConCmd( "mimi_shot_bots" )]
	static void Bots( int count, int nominations = 0 )
	{
		var lc = LobbyController.Current;
		if ( !lc.IsValid() ) { Log.Warning( "mimi_shot_bots: no LobbyController." ); return; }
		lc.BotCount = count;
		lc.BotNominations = nominations;
		lc.BotRandomLooks = true;
		Log.Info( $"mimi_shot_bots: BotCount={count} nominations={nominations}" );
	}

	/// <summary>Edit session: select brush N (-1 clears), so the gizmo / palette / sliders show.</summary>
	[ConCmd( "mimi_shot_select" )]
	static void Select( int index )
	{
		var s = SculptEditSession.Current;
		if ( !s.IsValid() || !s.IsEditing ) { Log.Warning( "mimi_shot_select: no edit session." ); return; }
		if ( index < 0 ) s.Deselect(); else s.Select( index );
	}

	/// <summary>Edit session: switch to Add-shape (stamp) mode, or back to the gizmo.</summary>
	[ConCmd( "mimi_shot_tool" )]
	static void Tool( string tool )
	{
		var s = SculptEditSession.Current;
		if ( !s.IsValid() || !s.IsEditing ) { Log.Warning( "mimi_shot_tool: no edit session." ); return; }
		s.SetTool( tool.ToLowerInvariant() == "add" ? SculptTool.Sculpt : SculptTool.Gizmo );
	}

	/// <summary>Force every edit session on the local hunter closed (tutorial included), no dialogs.</summary>
	[ConCmd( "mimi_shot_editoff" )]
	static void EditOff()
	{
		var meH = Connection.Local?.Id;
		var hider = Game.ActiveScene.GetAllComponents<HiderController>()
			.FirstOrDefault( h => !h.IsProxy && RoundManager.RosterIdOf( h.GameObject ) == meH );
		if ( hider.IsValid() ) { if ( hider.EditMode ) hider.ExitEditing(); return; }
		var me = Connection.Local?.Id;
		var hunter = Game.ActiveScene.GetAllComponents<HunterController>()
			.FirstOrDefault( h => h.Owned && RoundManager.RosterIdOf( h.GameObject ) == me );
		if ( !hunter.IsValid() ) { Log.Warning( "mimi_shot_editoff: no owned hunter pawn." ); return; }
		if ( hunter.ExternalSession.IsValid() && hunter.ExternalSession.IsEditing ) hunter.ExternalSession.SetActive( false );
		foreach ( var s in hunter.GameObject.Components.GetAll<SculptEditSession>( FindMode.EverythingInSelfAndDescendants ) )
			if ( s.IsEditing ) s.SetActive( false );
	}
}
