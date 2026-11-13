using System;
using System.Linq;
using System.Threading.Tasks;
using Mimiclay.Sim;

namespace Mimiclay;

/// <summary>
/// SimLab's EDIT test (2026-11-13): does sculpting a prop against the world agree on every machine? A player prop's owner
/// presses a brush of its own disguise into the floor / a wall — the edit PUSHES the prop out of the way
/// (BrushWorldClamp's prop push), the commit's origin rebase folds that into the pawn (HiderController.
/// RecenterOriginOnShape) and, for a client, sends the move to the host (RequestSimShift). Driven from sbox-cli
/// run_csharp, like the rest of the lab: <see cref="Walk"/> and <see cref="Act"/> on the owner's client, <see cref="Probe"/>
/// on every machine — the CLAY's world position (its feet) should match everywhere, before and after the prop next walks.
/// </summary>
public static class SimLabEdit
{
	static Scene S => Game.ActiveScene;

	static HiderController Mine => S.GetAllComponents<HiderController>()
		.FirstOrDefault( h => !h.IsProxy && !PropClaims.IsReleased( h ) && h.Network.Owner == Connection.Local );

	static HiderController PropOf( string id ) => S.GetAllComponents<HiderController>()
		.FirstOrDefault( h => !PropClaims.IsReleased( h ) && h.Network.Owner is { } o && o.Id.ToString() == id );

	static string V( Vector3 v ) => $"{v.x:0.0},{v.y:0.0},{v.z:0.0}";

	/// <summary>Any machine: where it has this player's prop — the clay's feet (bounds bottom-centre, world), the root, the
	/// sim body, the disguise's place under the root.</summary>
	public static string Probe( string id )
	{
		var h = PropOf( id );
		if ( h is null )
			return "no prop";
		h.TryGetShapeFeet( out var feet );
		var sim = S.GetSystem<SimWorld>()?.Find( h.GameObject.Id );
		var d = h.DisguiseSculpture;
		return $"feet {V( feet )} | root {V( h.WorldPosition )} | sim {(sim?.Phys is { } p && p.IsValid() ? V( p.Position ) : "-")}"
			+ $" | disguise local {(d.IsValid() ? V( d.GameObject.LocalPosition ) : "-")} | brushes {d?.Brushes?.Count ?? 0}"
			+ $" | editing {h.NetEditing} proxy {h.IsProxy}";
	}

	/// <summary>The owner: walk toward a world point for a while (the lab's debug walk), then stand.</summary>
	public static async Task<string> Walk( float x, float y, float z, float seconds )
	{
		var h = Mine;
		if ( h is null )
			return "no prop of mine";
		HiderController.DebugGoto = new Vector3( x, y, z );
		await GameTask.DelaySeconds( seconds );
		HiderController.DebugGoto = null;
		await GameTask.DelaySeconds( 1f );
		return $"walked to {V( h.WorldPosition )}";
	}

	/// <summary>The owner: open the editor on its own disguise, move brush 0 by (dx,dy,dz) WORLD units over
	/// <paramref name="frames"/> preview frames (a gizmo drag, as the session sees it), commit, close the editor.</summary>
	public static async Task<string> Act( float dx, float dy, float dz, int frames = 30 )
	{
		var h = Mine;
		if ( h is null )
			return "no prop of mine";
		var sculpt = h.DisguiseSculpture;
		if ( !sculpt.IsValid() || sculpt.Brushes is not { Count: > 0 } )
			return "no disguise";

		h.TryGetShapeFeet( out var feet0 );
		var root0 = h.WorldPosition;

		h.EnterEditing();
		for ( int i = 0; i < 20; i++ )
			await GameTask.Yield();
		var s = SculptEditSession.Current;
		if ( s is null || !s.IsEditing || s.Target != sculpt )
			return $"edit didn't open (session {s is not null}, editing {s?.IsEditing}, target ok {s?.Target == sculpt})";

		s.SetTool( SculptTool.Gizmo );
		s.Select( 0 );
		var b = s.SelectedBrush;
		if ( b is null )
			return "no brush selected";

		var step = sculpt.WorldTransform.Rotation.Inverse * (new Vector3( dx, dy, dz ) / MathF.Max( 1, frames )) / sculpt.WorldTransform.Scale.x;
		var asked0 = b.Position;
		float worstPush = 0f;
		for ( int i = 0; i < frames; i++ )
		{
			b.Position += step;
			s.PreviewChanged();
			worstPush = MathF.Max( worstPush, h.EditPushWorld.Length );
			await GameTask.Yield();
		}
		var moved = b.Position - asked0;
		var push = h.EditPushWorld;
		h.TryGetShapeFeet( out var feetDrag );

		s.CommitChanged();
		for ( int i = 0; i < 30; i++ )
			await GameTask.Yield();
		var pushAfter = h.EditPushWorld;
		var bounds = sculpt.GameObject.Components.Get<SculptBounds>();
		bool valid = !bounds.IsValid() || bounds.EvaluateNow();
		bool embedded = BrushWorldClamp.EmbeddedInWorld( sculpt );

		h.ExitEditing();
		await GameTask.DelaySeconds( 1.5f );
		h.TryGetShapeFeet( out var feet1 );

		return $"brush moved {V( moved )} local (asked {V( step * frames )}) | push at release {V( push )} (max {worstPush:0.0}), left after commit {V( pushAfter )}"
			+ $" | valid {valid} embedded {embedded}"
			+ $" | feet {V( feet0 )} -> drag {V( feetDrag )} -> end {V( feet1 )} | root {V( root0 )} -> {V( h.WorldPosition )}";
	}
}
