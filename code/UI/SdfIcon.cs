using System;
using System.Collections.Generic;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// Displays an SDF subject as a plain image. It owns no rendering of its own — it asks
/// <see cref="SdfIconFarm"/> for the texture and sets it as its background, so it's an ordinary div as far as
/// layout is concerned. Size it, clip it, round its corners, put ten of them in a row; none of that touches
/// the render.
///
/// Two icons sharing a <see cref="Key"/> share one render. That's the point of the split: the same head can be
/// in the round HUD and a scoreboard at once and only be drawn once.
/// </summary>
public sealed class SdfIcon : Panel
{
	/// <summary>Identity of the subject — anything with the same key shares a render. Defaults to this panel,
	/// meaning "don't share with anyone", which is the safe assumption but wastes the whole point.</summary>
	public object Key { get; set; }

	/// <summary>Live sculpture to portray. Updates on commit rather than following an edit in progress.</summary>
	public SdfSculpture Source { get; set; }

	/// <summary>Static brushes to portray when there's no <see cref="Source"/>.</summary>
	public List<SdfBrush> Brushes { get; set; }

	/// <summary>
	/// The icon's size in pixels — the size you're THINKING in, which everything else is quoted against. 0
	/// measures the laid-out panel instead, which is what the HUD does.
	/// </summary>
	public float IconSize { get; set; }

	/// <summary>
	/// Render at this multiple of <see cref="IconSize"/> and let the UI downsample it. A raymarched silhouette
	/// has no anti-aliasing of its own, so rendering 1:1 gives a hard, crawling edge.
	/// <para>
	/// Costs the square — 2× is 4× the pixels, 4× is 16×. It does NOT change how the icon looks, only how
	/// cleanly: a 512 icon with a 10px outline at 2× renders 1024 with a 20px outline, which downsamples back
	/// to the same picture.
	/// </para>
	/// </summary>
	public float Supersample { get; set; } = 4f;

	/// <summary>Ceiling on the final render resolution, so a large icon at a high multiple can't ask for
	/// something absurd.</summary>
	public int MaxRenderSize { get; set; } = 1024;

	/// <summary>Ink outline override, in PIXELS at <see cref="IconSize"/> — quoted the way you'd describe it,
	/// not as a fraction. 0 keeps whatever the rig prefab authored. Supersampling is applied for you.</summary>
	public float OutlineWidth { get; set; }

	/// <summary>Ink colour override — a red-outlined hunter and a green-outlined prop off the same rig, say.
	/// Null keeps the rig's. Each distinct combination is its own render, so vary this per ROLE or per state,
	/// not per icon.</summary>
	public Color? OutlineColor { get; set; }

	/// <summary>Render the subject as a flat grey silhouette — the ink outline still draws in its usual colour,
	/// so it wears the same ring every portrait does. Its own render, cached separately from the lit one.</summary>
	public bool Silhouette { get; set; }

	/// <summary>Camera orientation override, in the subject's local space (a prop disguise portrays from the
	/// angle its player last edited it at). Null keeps the rig prefab's pose. Per subject, not per icon —
	/// see the note on <see cref="SdfIconRequest.Pose"/>.</summary>
	public Angles? Pose { get; set; }

	/// <summary>Hold the current picture while this subject's player is mid-edit; it catches up in one render
	/// when cleared. See <see cref="SdfThumbnail.Frozen"/>.</summary>
	public bool Frozen { get; set; }

	Texture _applied;
	SdfIconFarm _farm;

	/// <summary>
	/// Work out what to ask the farm for. Everything the caller states in pixels is relative to the icon's
	/// logical size, so converting to the shader's fraction-of-image drops the supersample factor out entirely —
	/// which is exactly why the outline survives it unchanged.
	/// </summary>
	bool Resolve( out int renderSize, out float? outlineFraction )
	{
		renderSize = 0;
		outlineFraction = null;

		// Assumes 1080p, where a CSS pixel and a real one are the same thing. Above that the UI scales and
		// Box.Rect grows, which only means a sharper render than asked for.
		var logical = IconSize;
		var stated = logical > 0f;

		if ( !stated )
		{
			logical = MathF.Max( Box.Rect.Width, Box.Rect.Height );
			if ( logical < 1f )
				return false; // not laid out yet — try again next frame
		}

		var wanted = logical * MathF.Max( Supersample, 1f );

		// Snapped to 64 ONLY when the size came from layout: it feeds the farm's cache key, so a pixel of drift
		// would otherwise mint a fresh render — and a fresh SceneWorld — every frame. A stated size can't drift,
		// so it's honoured exactly: say 512 at 2× and you get 1024.
		renderSize = Math.Clamp(
			stated ? (int)MathF.Round( wanted ) : (int)(MathF.Ceiling( wanted / 64f ) * 64f),
			64, MaxRenderSize );

		if ( OutlineWidth > 0f )
			outlineFraction = OutlineWidth / logical;

		return true;
	}

	public override void Tick()
	{
		base.Tick();

		// Resolved from our own panel tree, and cached until it dies — a scene change tears the whole tree down
		// and the next lookup finds the new one.
		if ( !_farm.IsValid() )
			_farm = SdfIconFarm.For( this );

		if ( _farm is null )
			return;

		// Asked for every frame, not just when the razor rebuilds: it's what keeps the farm entry alive against
		// pruning, and it's how we pick the texture up on the frame it first exists rather than waiting for
		// whatever unrelated thing next triggers a rebuild.
		if ( !Resolve( out var renderSize, out var outlineFraction ) )
			return;

		var texture = _farm.TextureFor( new SdfIconRequest
		{
			Key = Key ?? this,
			RenderSize = renderSize,
			Source = Source,
			Brushes = Brushes,
			OutlineColor = OutlineColor,
			OutlineWidth = outlineFraction,
			Silhouette = Silhouette,
			Pose = Pose,
			Frozen = Frozen,
		} );
		if ( texture == _applied )
			return;

		_applied = texture;
		Style.BackgroundImage = texture;
	}
}
