using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Mimiclay;

/// <summary>
/// <c>mimi_dbg_plates</c> — one-shot dump of everything a nameplate is built from, for the 2026-10 playtest
/// reports of plates floating away from their pawn (a hunter's name not over the hunter's head; Teams prop
/// names not over the prop). The bug never reproduced on loopback, where both hunters' plates sat exactly
/// over their heads, so this exists to be run ON THE MACHINE THAT SEES IT, mid-round, and pasted back.
///
/// Per pawn controller in the scene it logs who the pawn belongs to, the concealment/proxy verdicts the
/// overlay gates on, the anchor the overlay projects (top of the body sculpture), where that sculpture's
/// renderer is ACTUALLY drawing (its placed scene object), and the anchor's screen projection — so the
/// three candidate explanations separate cleanly:
/// <list type="bullet">
/// <item>anchor ≠ drawn centre → the renderer and the GameObject disagree (a placement bug);</item>
/// <item>two pawn objects answer to the same roster row → a stale copy (a dropped destroy, the engine's
/// mid-load message drop — see RoundManager's reconcile), and the plate is on the ghost;</item>
/// <item>everything agrees but the screen point is off → projection (camera / viewport / UI scale).</item>
/// </list>
/// Also lists every "Pawn (…)" object INCLUDING disabled ones, since a ghost may be a disabled copy that no
/// GetAllComponents walk sees.
/// </summary>
public static class NameplateDebug
{
	[ConCmd( "mimi_dbg_plates" )]
	static void Dump()
	{
		var scene = Game.ActiveScene;
		if ( !scene.IsValid() )
		{
			Log.Warning( "mimi_dbg_plates: no active scene." );
			return;
		}

		var sb = new StringBuilder();
		var cam = scene.Camera;
		var me = Connection.Local;

		sb.AppendLine( "── mimi_dbg_plates ──" );
		sb.AppendLine( $"local={me?.DisplayName ?? "?"} ({me?.Id}) host={Networking.IsHost} active={Networking.IsActive}" );

		if ( RoundManager.Current.IsValid() )
		{
			var rm = RoundManager.Current;
			sb.AppendLine( $"round: phase={rm.Phase} mode={rm.Settings.Mode} localRole={RoundManager.LocalRole} " +
				$"huntersConcealed={RoundManager.HuntersConcealed} propsConcealed={RoundManager.PropsConcealed} " +
				$"rows={rm.Players.Count}" );
			foreach ( var kv in rm.Players )
				sb.AppendLine( $"  row {kv.Key}: {kv.Value.Name} role={kv.Value.Role} alive={kv.Value.Alive} bot={kv.Value.Bot} pawnId={kv.Value.PawnId}" );
		}
		else
		{
			sb.AppendLine( "round: none (lobby / creative / charades)" );
		}

		if ( cam.IsValid() )
			sb.AppendLine( $"camera: pos={cam.WorldPosition} ang={cam.WorldRotation.Angles()} fov={cam.FieldOfView} axis={cam.FovAxis} screen={Screen.Size} viewport={cam.Viewport}" );
		else
			sb.AppendLine( "camera: NONE" );

		var overlay = PlayerNameplates.Current;
		if ( overlay.IsValid() )
		{
			sb.AppendLine( $"overlay: '{overlay.GameObject.Name}' plates={overlay.Plates.Count} scale={overlay.NameplateScale}" );
			foreach ( var p in overlay.Plates )
				sb.AppendLine( $"  plate '{p.Name}' pawn={p.PawnId} screen=({p.Screen.x:0.000},{p.Screen.y:0.000}) speaking={p.Speaking}" );
		}
		else
		{
			sb.AppendLine( "overlay: NONE (PlayerNameplates.Current invalid)" );
		}

		// Every pawn controller the overlay could enumerate, with the anchor it would project.
		var byRoster = new Dictionary<Guid, List<string>>();

		foreach ( var h in scene.GetAllComponents<HunterController>() )
			Describe( sb, cam, h.GameObject, "Hunter", h.Face, h.IsProxy, h.Concealed, byRoster );

		foreach ( var h in scene.GetAllComponents<HiderController>() )
			Describe( sb, cam, h.GameObject, "Prop", h.DisguiseSculpture, h.IsProxy, h.Concealed, byRoster );

		foreach ( var kv in byRoster.Where( kv => kv.Value.Count > 1 ) )
			sb.AppendLine( $"!! DUPLICATE pawns for roster {kv.Key}: {string.Join( ", ", kv.Value )}" );

		// Everything named like a pawn, disabled copies included — a ghost that was switched off is invisible
		// to the GetAllComponents walks above but still answers here.
		var all = scene.GetAllObjects( true ).Where( o => o.Name.StartsWith( "Pawn (", StringComparison.Ordinal ) ).ToList();
		sb.AppendLine( $"pawn objects (incl. disabled): {all.Count}" );
		foreach ( var o in all )
			sb.AppendLine( $"  {o.Name} id={o.Id} enabled={o.Enabled} active={o.Active} pos={o.WorldPosition} owner={o.Network.Owner?.DisplayName ?? "-"} netActive={o.Network.Active}" );

		Log.Info( sb.ToString() );
	}

	static void Describe( StringBuilder sb, CameraComponent cam, GameObject pawn, string kind, SdfSculpture sculpt,
		bool isProxy, bool concealed, Dictionary<Guid, List<string>> byRoster )
	{
		var rosterId = RoundManager.RosterIdOf( pawn );
		var rowName = rosterId is { } rid && RoundManager.Current.IsValid()
			&& RoundManager.Current.Players.TryGetValue( rid, out var info ) ? info.Name : null;

		sb.AppendLine( $"{kind} '{pawn.Name}' id={pawn.Id} roster={rosterId?.ToString() ?? "-"} row='{rowName ?? "-"}' " +
			$"owner={pawn.Network.Owner?.DisplayName ?? "-"} proxy={isProxy} bot={RoundManager.IsBotPawn( pawn )} concealed={concealed} " +
			$"enabled={pawn.Enabled} active={pawn.Active} root={pawn.WorldPosition}" );

		if ( rosterId is { } r )
		{
			if ( !byRoster.TryGetValue( r, out var list ) )
				byRoster[r] = list = new List<string>();
			list.Add( $"{pawn.Name} ({pawn.Id})" );
		}

		if ( !sculpt.IsValid() )
		{
			sb.AppendLine( "    sculpture: NONE → anchor falls back to root + 80" );
			return;
		}

		var anchor = PlayerNameplates.DebugAnchorFor( pawn );
		int brushes = sculpt.Brushes?.Count ?? 0;
		sb.AppendLine( $"    sculpture '{sculpt.GameObject.Name}' active={sculpt.Active} brushes={brushes} pos={sculpt.WorldPosition} anchor={anchor}" );

		var r2 = sculpt.Components.Get<SdfRaymarchRenderer>( includeDisabled: true );
		if ( r2.IsValid() )
		{
			var drawn = r2.DrawnTransform;
			float drift = r2.DrawnSceneObjectLive ? (drawn.Position - sculpt.WorldPosition).Length : -1f;
			sb.AppendLine( $"    renderer enabled={r2.Enabled} live={r2.DrawnSceneObjectLive} drawnPos={drawn.Position} drift={drift:0.0} " +
				$"drawnCenter={r2.DrawnCenter} drawnBounds={r2.DrawnBounds.Mins}..{r2.DrawnBounds.Maxs} fieldPending={r2.FieldPending}" );
		}
		else
		{
			sb.AppendLine( "    renderer: NONE on the sculpture object" );
		}

		if ( cam.IsValid() )
		{
			var screen = cam.PointToScreenNormal( anchor, out var behind );
			sb.AppendLine( $"    screen=({screen.x:0.000},{screen.y:0.000}) behind={behind}" );
		}
	}
}
