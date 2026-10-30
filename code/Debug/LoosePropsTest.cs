using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Test spot for walking among rigid bodies (HiderController.Walk): <c>mimi_dbg_loose_props</c> drops a mix of
/// loose rigid bodies in front of the local prop — light and heavy boxes, rolling balls, planks to stand on, a small
/// stack — and <c>mimi_dbg_loose_props_clear</c> removes them. Local and unnetworked: a single-player test bed.
/// </summary>
public static class LoosePropsTest
{
	const string Tag = "mimi_loose_test";

	[ConCmd( "mimi_dbg_loose_props", Help = "Debug: spawn loose rigid bodies (boxes, balls, planks, a stack) in front of your prop." )]
	public static void Spawn()
	{
		var scene = Game.ActiveScene;
		if ( scene is null )
			return;
		var me = scene.GetAllComponents<HiderController>().FirstOrDefault( h => !h.IsProxy && h.Network.IsOwner )
			?? scene.GetAllComponents<HiderController>().FirstOrDefault( h => !h.IsProxy );
		var origin = me.IsValid() ? me.WorldPosition : Vector3.Zero;
		var forward = me.IsValid() ? Rotation.FromYaw( me.WorldRotation.Angles().yaw ).Forward : Vector3.Forward;
		var right = Vector3.Cross( forward, Vector3.Up ).Normal;

		Vector3 At( float ahead, float side, float up ) => origin + forward * ahead + right * side + Vector3.Up * up;

		// Light boxes to bump through, a heavy one to be stopped by, balls to roll, planks to ride, a stack to wade into.
		for ( int i = 0; i < 6; i++ )
			Box( scene, At( 90f + i * 22f, -60f + (i % 3) * 40f, 10f ), new Vector3( 12f + i * 2f ), 1f );
		Box( scene, At( 120f, 70f, 30f ), new Vector3( 48f ), 4f ); // heavy: 4× clay density
		for ( int i = 0; i < 4; i++ )
			Ball( scene, At( 200f, -40f + i * 26f, 12f ), 9f + i * 2f, 1f );
		Box( scene, At( 260f, -40f, 6f ), new Vector3( 80f, 24f, 4f ), 1f );
		Box( scene, At( 260f, 20f, 6f ), new Vector3( 80f, 24f, 4f ), 1f );
		for ( int i = 0; i < 4; i++ )
			Box( scene, At( 330f, 0f, 10f + i * 21f ), new Vector3( 20f ), 1f );

		Log.Info( $"[loose props] spawned in front of {(me.IsValid() ? me.GameObject.Name : "the origin")} — mimi_dbg_loose_props_clear removes them" );
	}

	[ConCmd( "mimi_dbg_loose_props_clear", Help = "Debug: remove the loose test bodies." )]
	public static void Clear()
	{
		var scene = Game.ActiveScene;
		if ( scene is null )
			return;
		foreach ( var go in scene.GetAllObjects( false ).Where( g => g.Tags.Has( Tag ) ).ToList() )
			go.Destroy();
	}

	// Masses follow the clay rule (SdfCollider.ClayMass: volume × clay density), so they compare honestly with a prop's
	// own body — the toy train weighs ~9000 that way. `density` scales it (the heavy box is 4×).
	static float ClayMass( float cubicUnits, float density )
		=> cubicUnits * (0.0254f * 0.0254f * 0.0254f) * (SdfCollider.ClaySurface?.Density ?? 1000f) * density;

	public static void Box( Scene scene, Vector3 pos, Vector3 size, float density )
	{
		float mass = ClayMass( size.x * size.y * size.z, density );
		var model = Model.Load( "models/dev/box.vmdl" );
		var go = new GameObject( true, "loose test box" );
		go.Tags.Add( Tag );
		go.WorldPosition = pos;
		var mb = model.Bounds;
		go.WorldScale = new Vector3( size.x / mb.Size.x, size.y / mb.Size.y, size.z / mb.Size.z );
		go.Components.Create<ModelRenderer>().Model = model;
		var col = go.Components.Create<BoxCollider>();
		col.Scale = mb.Size;
		col.Center = mb.Center;
		go.Components.Create<Rigidbody>().MassOverride = mass;
	}

	public static void Ball( Scene scene, Vector3 pos, float radius, float density )
	{
		float mass = ClayMass( 4f / 3f * MathF.PI * radius * radius * radius, density );
		var model = Model.Load( "models/dev/sphere.vmdl" );
		var go = new GameObject( true, "loose test ball" );
		go.Tags.Add( Tag );
		go.WorldPosition = pos;
		var mb = model.Bounds;
		go.WorldScale = new Vector3( radius * 2f / mb.Size.x );
		go.Components.Create<ModelRenderer>().Model = model;
		var col = go.Components.Create<SphereCollider>();
		col.Radius = mb.Size.x * 0.5f;
		col.Center = mb.Center;
		go.Components.Create<Rigidbody>().MassOverride = mass;
	}
}
