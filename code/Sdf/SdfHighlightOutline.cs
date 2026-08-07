using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Rendering;

namespace Mimiclay;

/// <summary>Where <see cref="SdfHighlightOutline"/> draws its line relative to the silhouette
/// (stroke alignment, like a design tool's inside/centre/outside stroke).</summary>
public enum SdfOutlinePlacement
{
	/// <summary>The line sits beyond the edge, over whatever is behind the prop (the engine
	/// HighlightOutline look). Its pixels carry the BACKGROUND's depth, so depth-driven post
	/// effects (DOF, fog) treat the line as background — a sharp prop can end up wearing a
	/// blurred halo.</summary>
	Outside,

	/// <summary>The line straddles the edge — half over the prop, half beyond it.</summary>
	Center,

	/// <summary>The line sits on the prop's own pixels, just inside the edge. Those pixels carry
	/// the PROP's depth, so DOF/fog treat the line as part of the prop — the right pick when such
	/// effects are running. Also immune to the proxy-padding width clamp (the line never leaves
	/// the silhouette).</summary>
	Inside,
}

/// <summary>
/// Highlight outline for SDF-raymarched props — the raymarch counterpart of the engine's
/// <c>HighlightOutline</c>, which re-draws mesh geometry with a stencil material and so could only
/// ever outline our proxy BOX, never the sculpted surface. Unlike the engine pair, NO camera-side
/// component is needed.
///
/// Targets every <see cref="SdfRaymarchRenderer"/> on this GameObject AND its descendants (like the
/// engine component's renderer scan, minus any that opt out via
/// <see cref="SdfRaymarchRenderer.ExcludeFromHighlight"/>), and draws them as ONE COMBINED silhouette: a single
/// translucent proxy box re-marches the UNION of the members' baked distance fields
/// (shaders/sdf_highlight.shader), so a multi-part pawn (hunter head + body) gets one continuous
/// outline with no interior lines or double-blend where the parts overlap. Rays that hit the union
/// take the inside fill; rays passing within <see cref="Width"/> screen-pixels take the outline;
/// the scene depth chain picks the visible vs obscured colour of each, so the obscured pair glows
/// through walls exactly like the engine component.
///
/// Field-cache only: the highlight marches the baked fields (keeps the shader free of a copy of
/// the analytic brush evaluator), so a member draws nothing while its first bake is in flight or
/// if it opts out of <see cref="SdfRaymarchRenderer.UseFieldCache"/>. At most
/// <see cref="MaxTargets"/> members are combined (shader slot count); extras are ignored with a
/// one-time warning. A renderer belongs to one highlight at a time (last scan wins).
/// </summary>
[Title( "SDF Highlight Outline" )]
[Category( "SDF" )]
[Icon( "highlight_alt" )]
public sealed class SdfHighlightOutline : Component, Component.ExecuteInEditor
{
	/// <summary>Shader field-slot count — must match the slot declarations in sdf_highlight.shader.</summary>
	public const int MaxTargets = 4;

	/// <summary>Outline colour where the silhouette is directly visible.</summary>
	[Property] public Color Color { get; set; } = Color.White;

	/// <summary>Outline colour where something nearer covers the group (through-wall glow). Transparent = none.</summary>
	[Property] public Color ObscuredColor { get; set; } = Color.Black * 0.4f;

	/// <summary>Fill over the visible surface. Transparent = outline only.</summary>
	[Property] public Color InsideColor { get; set; } = Color.Transparent;

	/// <summary>Fill where the surface is covered by something nearer.</summary>
	[Property] public Color InsideObscuredColor { get; set; } = Color.Transparent;

	/// <summary>Outline width in screen PIXELS (constant on screen at any distance). Outside/Center
	/// placement is clipped where the line would leave the proxy geometry (~2" of bounds padding),
	/// so keep it a few px there; Inside placement has no such limit.</summary>
	[Property, Range( 0f, 16f )] public float Width { get; set; } = 4f;

	/// <summary>Stroke alignment: draw the line beyond the silhouette (Outside, the engine look),
	/// straddling it (Center), or on the prop's own pixels (Inside — plays nicest with DOF/fog,
	/// since the line then shares the prop's depth).</summary>
	[Property] public SdfOutlinePlacement Placement { get; set; } = SdfOutlinePlacement.Outside;

	/// <summary>Draw the highlight AFTER depth-of-field, so the line stays crisp even when the prop
	/// is out of focus. Uses the engine HighlightOutline's own slot (AfterTransparent order 1000 —
	/// DOF runs at order 100), so bloom/tonemapping still apply and colours match the normal path.
	/// Needs the game camera: in the editor's scene view it falls back to the normal translucent
	/// pass (which DOF affects).</summary>
	[Property] public bool IgnoreDepthOfField { get; set; }

	/// <summary>Runtime hide gate: true = draw nothing, but the component stays ENABLED — it keeps
	/// ticking and stays findable through Scene.GetAllComponents (which only enumerates enabled
	/// components, so a gate that disabled the component could never find it again to re-show it).
	/// The highlight counterpart of <see cref="SdfRaymarchRenderer.RenderHidden"/>; set per machine
	/// by whoever owns the visibility rules (see RoundOutlineSystem). Not serialized — author
	/// persistent state with <see cref="Component.Enabled"/> instead.</summary>
	public bool Hidden { get; set; }

	/// <summary>Runtime look overrides — a non-null slot replaces its authored [Property] counterpart
	/// for as long as it's set. Same contract as <see cref="Hidden"/>: driven per machine at runtime
	/// (see <c>SdfOutlineFlash</c>, the Reveal pulse), NEVER serialized — so a network snapshot can
	/// only ever ship the authored look, and clearing back to null is a perfect restore.</summary>
	public Color? ColorOverride { get; set; }
	/// <inheritdoc cref="ColorOverride"/>
	public Color? ObscuredColorOverride { get; set; }
	/// <inheritdoc cref="ColorOverride"/>
	public Color? InsideColorOverride { get; set; }
	/// <inheritdoc cref="ColorOverride"/>
	public Color? InsideObscuredColorOverride { get; set; }
	/// <inheritdoc cref="ColorOverride"/>
	public float? WidthOverride { get; set; }

	/// <summary>Track the plasticine displacement lumps on members that have Displace on, so the
	/// line hugs the LUMPY silhouette instead of the smooth baked one (the noise is not in the
	/// field — the main shader adds it per pixel, and so does the highlight when this is on).
	/// Free when no member displaces; on displacing props it adds the same noise + understep cost
	/// per march step that the main shader already pays. Off = the smooth-silhouette outline
	/// (which can show slivers of clay past an Inside line at each outward lump).</summary>
	[Property] public bool MatchDisplacement { get; set; } = true;

	SceneObject _so;
	Material _material;
	readonly List<SdfRaymarchRenderer> _targets = new();
	readonly List<SdfRaymarchRenderer> _readyScratch = new(); // reused per sync (pinged once per member per frame)
	int _lastBuildHash;
	BBox _worldBounds;
	bool _warnedOverflow;

	// IgnoreDepthOfField path: the proxy is drawn through a camera command list in the engine
	// Highlight's post-DOF slot instead of the translucent pass. The scene object stays as the
	// model + attribute holder either way — only who submits the draw changes.
	CommandList _commands;
	CameraComponent _cmdCamera; // camera the command list is currently attached to

	protected override void OnEnabled()
	{
		_lastBuildHash = 0;
		ScanTargets();
	}

	protected override void OnDisabled()
	{
		foreach ( var r in _targets )
		{
			if ( r.IsValid() && r.Highlight == this )
				r.Highlight = null;
		}
		_targets.Clear();
		Release();
	}

	// Backstop like the renderer's: _so lives in Scene.SceneWorld, not the GameObject graph, so it
	// must be deleted explicitly or it outlives the object (OnDisabled isn't guaranteed for every
	// bulk-teardown path).
	protected override void OnDestroy() => Release();

	void Release()
	{
		DetachCommands();
		_so?.Delete();
		_so = null;
	}

	// Everything that can be showing this highlight, off. (The command list keeps replaying its
	// recorded draw every frame until reset — hiding the scene object alone isn't enough.)
	void HideVisual()
	{
		if ( _so.IsValid() )
			_so.RenderingEnabled = false;
		_commands?.Reset();
	}

	void AttachCommands( CameraComponent cam )
	{
		_commands ??= new CommandList( "SdfHighlightOutline" );
		if ( _cmdCamera == cam )
			return;

		DetachCommands();
		// The engine Highlight's slot: DOF inserts at AfterTransparent order 100, Highlight at
		// order 1000 — drawing there is exactly how engine outlines stay crisp through DOF.
		// Bloom (BeforePostProcess) and tonemapping run later, so colours match the normal path.
		cam.AddCommandList( _commands, Stage.AfterTransparent, 1000 );
		_cmdCamera = cam;
	}

	void DetachCommands()
	{
		_commands?.Reset();
		if ( _cmdCamera.IsValid() && _commands is not null )
			_cmdCamera.RemoveCommandList( _commands );
		_cmdCamera = null;
	}

	// Re-resolve the group each frame (renderers can be added/removed under us at runtime — the
	// engine component re-queries its targets per frame too) and keep their back-references
	// current: each member pings SyncFromRenderer from ITS OnUpdate.
	void ScanTargets()
	{
		var found = Components.GetAll<SdfRaymarchRenderer>( FindMode.EnabledInSelfAndDescendants )
			.Where( r => !r.ExcludeFromHighlight ).ToList();

		foreach ( var old in _targets )
		{
			if ( old.IsValid() && old.Highlight == this && !found.Contains( old ) )
				old.Highlight = null;
		}

		if ( found.Count > MaxTargets )
		{
			if ( !_warnedOverflow )
			{
				_warnedOverflow = true;
				Log.Warning( $"[SdfHighlightOutline] {GameObject.Name}: {found.Count} SDF renderers under the highlight, shader supports {MaxTargets} — extras won't be outlined." );
			}
			found.RemoveRange( MaxTargets, found.Count - MaxTargets );
		}

		_targets.Clear();
		foreach ( var r in found )
		{
			r.Highlight = this;
			_targets.Add( r );
		}
	}

	// The real work happens in SyncFromRenderer, called by each member renderer at the end of ITS
	// OnUpdate — the last member to update each frame leaves the mirror fully same-frame coherent
	// (our own tick order vs theirs is undefined, and a frame-old mirror jitters against the live
	// surface while sculpting). This tick only maintains the group and hides a stale outline when
	// nothing is left to drive a sync.
	protected override void OnUpdate()
	{
		ScanTargets();

		bool anyLive = false;
		foreach ( var r in _targets )
		{
			if ( r.IsValid() && r.Enabled )
			{
				anyLive = true;
				break;
			}
		}

		if ( !anyLive )
			HideVisual();
	}

	internal void SyncFromRenderer( SdfRaymarchRenderer _ )
	{
		// Effective look: authored [Property] values unless a runtime override slot is set.
		var color = ColorOverride ?? Color;
		var obscuredColor = ObscuredColorOverride ?? ObscuredColor;
		var insideColor = InsideColorOverride ?? InsideColor;
		var insideObscuredColor = InsideObscuredColorOverride ?? InsideObscuredColor;
		float width = WidthOverride ?? Width;

		// Anything to draw at all? (Fully-transparent colours or zero width = nothing.)
		bool wantsOutline = width > 0f && (color.a > 0f || obscuredColor.a > 0f);
		bool wantsFill = insideColor.a > 0f || insideObscuredColor.a > 0f;

		// Members that can march this frame (field baked, not force-hidden). The rest of the group
		// joins as it becomes ready — one partial frame at spawn, self-correcting.
		var ready = _readyScratch;
		ready.Clear();
		foreach ( var r in _targets )
		{
			if ( r.IsValid() && r.Enabled && r.HighlightReady && r.HighlightVisible )
				ready.Add( r );
		}

		bool show = !Hidden && (wantsOutline || wantsFill) && ready.Count > 0;
		if ( !show )
		{
			HideVisual();
			return;
		}

		_material ??= Material.FromShader( "shaders/sdf_highlight.shader" );

		// (Re)build the single group proxy whenever any member rebuilt its own (brush edit, move…)
		// or the membership changed. One box for the whole group: union each member's padded local
		// proxy corners, folded into OUR local frame so the box stays oriented to the pawn instead
		// of bloating into a world-axis cube when it rotates.
		var h = new HashCode();
		h.Add( ready.Count );
		foreach ( var r in ready )
		{
			h.Add( r.Id );
			h.Add( r.ProxyVersion );
		}
		int hash = h.ToHashCode();

		if ( !_so.IsValid() || _lastBuildHash != hash )
		{
			var myTx = WorldTransform;
			var lmins = new Vector3( float.MaxValue );
			var lmaxs = new Vector3( float.MinValue );

			foreach ( var r in ready )
			{
				var rtx = r.WorldTransform;
				var rmins = r.ProxyLocalMins;
				var rmaxs = r.ProxyLocalMaxs;
				for ( int c = 0; c < 8; c++ )
				{
					var corner = new Vector3(
						(c & 1) != 0 ? rmaxs.x : rmins.x,
						(c & 2) != 0 ? rmaxs.y : rmins.y,
						(c & 4) != 0 ? rmaxs.z : rmins.z );

					var local = myTx.PointToLocal( rtx.PointToWorld( corner ) );
					lmins = Vector3.Min( lmins, local );
					lmaxs = Vector3.Max( lmaxs, local );
				}
			}

			var model = SdfRaymarchRenderer.BuildHighlightProxyBox( lmins, lmaxs, myTx, _material, out _worldBounds );

			if ( _so.IsValid() )
			{
				_so.Model = model;
			}
			else
			{
				_so = new SceneObject( Scene.SceneWorld, model );
				_so.Batchable = false; // per-object attributes, same reason as the raymarch scene object
				_so.Flags.CastShadows = false;
			}

			_so.Bounds = _worldBounds;
			_lastBuildHash = hash;
		}

		// Shared march state: the union bracket + the most demanding member's budget.
		int maxSteps = 0;
		float epsilon = float.MaxValue;
		foreach ( var r in ready )
		{
			maxSteps = Math.Max( maxSteps, r.MaxSteps );
			epsilon = MathF.Min( epsilon, r.Epsilon );
		}

		var a = _so.Attributes;
		a.Set( "FieldCount", ready.Count );
		a.Set( "BoundsMin", _worldBounds.Mins );
		a.Set( "BoundsMax", _worldBounds.Maxs );
		a.Set( "MaxSteps", maxSteps );
		a.Set( "Epsilon", epsilon );

		a.Set( "OutlineColor", color );
		a.Set( "OutlineObscuredColor", obscuredColor );
		a.Set( "InsideColor", insideColor );
		a.Set( "InsideObscuredColor", insideObscuredColor );
		a.Set( "LineWidthPx", width );
		// Placement = how far (px) the shader erodes the union field before marching, which slides
		// the whole band across the silhouette (see ErodePx in the shader).
		a.Set( "ErodePx", Placement switch
		{
			SdfOutlinePlacement.Inside => width,
			SdfOutlinePlacement.Center => width * 0.5f,
			_ => 0f,
		} );
		a.Set( "PixelScale", ComputePixelScale() );

		// (Claymation boil is per-member now, pushed into each slot by ApplyHighlightAttributes.)

		// Each member's field bindings + model fold into its shader slot.
		for ( int slot = 0; slot < ready.Count; slot++ )
			ready[slot].ApplyHighlightAttributes( _so, slot, MatchDisplacement );

		// Zero the displacement of unoccupied slots: attributes persist, and a stale amp from a
		// member that left the group would keep inflating the march's understep budget (the shader
		// reads all four amps for its step-safety factor, occupied or not).
		for ( int slot = ready.Count; slot < MaxTargets; slot++ )
			a.Set( $"DispAmp{slot}", 0f );

		// Route the draw. Normally the scene object renders itself in the translucent pass (which
		// DOF then blurs); with IgnoreDepthOfField the same model + attributes are submitted
		// through the post-DOF command list instead. Falls back to the scene object when there's
		// no game camera to attach to (editor scene view).
		var cam = IgnoreDepthOfField && !Scene.IsEditor ? Scene.Camera : null;
		if ( cam.IsValid() )
		{
			_so.RenderingEnabled = false;
			AttachCommands( cam );
			_commands.Reset();
			// Identity transform: the proxy mesh is built with world-space vertices.
			_commands.DrawModel( _so.Model, global::Transform.Zero, _so.Attributes );
		}
		else
		{
			DetachCommands();
			_so.RenderingEnabled = true;
		}
	}

	// World units one screen pixel spans at unit distance from the camera. The shader multiplies by
	// the ray distance to turn the fields' world distances into screen pixels, which is what keeps
	// the outline a constant on-screen width. Falls back to 90° when there's no camera (editor view).
	float ComputePixelScale()
	{
		var cam = Scene.Camera;
		float fov = cam.IsValid() ? cam.FieldOfView : 90f;
		bool vertical = cam.IsValid() && cam.FovAxis == CameraComponent.Axis.Vertical;
		float extent = vertical ? Screen.Height : Screen.Width;
		if ( extent <= 0f )
			return 0.001f;

		return 2f * MathF.Tan( fov.DegreeToRadian() * 0.5f ) / extent;
	}
}
