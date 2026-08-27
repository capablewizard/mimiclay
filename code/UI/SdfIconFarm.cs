using System;
using System.Collections.Generic;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// Renders SDF icons once, off-screen, and hands out the resulting textures — so the same head can appear in
/// the round HUD, a scoreboard and a results screen off ONE render instead of three.
///
/// This exists because a <see cref="SdfThumbnail"/> is a ScenePanel, which sizes its render target from its own
/// layout box: as a visible icon it can only ever render at the size it's displayed. Parking the renderers
/// off-screen at a fixed resolution decouples the two, which is what makes both reuse and supersampling
/// possible (icons display far smaller than 256px and the UI filters the difference).
///
/// Put one of these in EACH UI root that shows icons — <see cref="SdfIcon"/> finds the one in its own tree via
/// <see cref="SdfIconFarm.For(Sandbox.UI.Panel)"/> — and put it early, so it exists before the icons that ask
/// it for textures.
/// </summary>
/// <summary>What to draw and how. Everything in here that can differ between two icons has to be part of the
/// farm's cache key, or icons sharing a <see cref="Key"/> would fight over one panel's settings.</summary>
public readonly struct SdfIconRequest
{
	/// <summary>Identity of the subject. Same key + same settings = one shared render.</summary>
	public object Key { get; init; }

	/// <summary>Resolution to render at, independent of display size.</summary>
	public int RenderSize { get; init; }

	/// <summary>Live sculpture — re-read on commit, not during a drag.</summary>
	public SdfSculpture Source { get; init; }

	/// <summary>Static brushes, used only when <see cref="Source"/> is null.</summary>
	public List<SdfBrush> Brushes { get; init; }

	/// <summary>Outline overrides. Null keeps whatever the rig prefab authored.</summary>
	public Color? OutlineColor { get; init; }

	/// <inheritdoc cref="OutlineColor"/>
	public float? OutlineWidth { get; init; }

	/// <summary>Render the subject as a flat grey silhouette (the ink outline still draws). Part of the cache
	/// key — the same subject lit and silhouetted is genuinely two renders.</summary>
	public bool Silhouette { get; init; }

	// Pose and Frozen are deliberately NOT in the cache key: they're the subject's current STATE, not a
	// different picture of the same subject — a key only ever has one pose and one freeze at a time, and
	// changing either should retarget the existing render (a reframe / a held picture), not mint a second
	// SceneWorld. Don't use them to show one subject two ways at once.

	/// <summary>Camera orientation override, in the subject's local space. Null keeps the rig prefab's.</summary>
	public Angles? Pose { get; init; }

	/// <summary>Hold the current picture — see <see cref="SdfThumbnail.Frozen"/>.</summary>
	public bool Frozen { get; init; }
}

public sealed class SdfIconFarm : Panel
{
	// Every live farm. A list rather than a single Current: each UI root (the round HUD, a menu, a sculpt
	// library) wants its own, and with one static the last one built would silently steal every other tree's
	// icons — they'd render into a farm that isn't in their panel tree.
	static readonly List<SdfIconFarm> Farms = new();

	/// <summary>
	/// The farm serving this panel's tree. Falls back to any live farm if the panel's own tree hasn't got one,
	/// which is better than drawing nothing — but the icons will be rendering somewhere unexpected, so give each
	/// UI root its own.
	/// </summary>
	public static SdfIconFarm For( Panel panel )
	{
		SdfIconFarm fallback = null;
		var root = RootOf( panel );

		for ( var i = Farms.Count - 1; i >= 0; i-- )
		{
			var farm = Farms[i];

			if ( !farm.IsValid() )
			{
				Farms.RemoveAt( i );
				continue;
			}

			if ( root is not null && RootOf( farm ) == root )
				return farm;

			fallback ??= farm;
		}

		return fallback;
	}

	static Panel RootOf( Panel panel )
	{
		while ( panel?.Parent is not null )
			panel = panel.Parent;

		return panel;
	}

	/// <summary>Seconds an unrequested render is kept before its panel (and its SceneWorld) is dropped. Needs to
	/// comfortably exceed the host's re-render interval — the HUD only rebuilds when its BuildHash changes, so
	/// a key can legitimately go a while between requests without being dead.</summary>
	public float PruneAfter { get; set; } = 10f;

	sealed class Entry
	{
		public SdfThumbnail Panel;
		public RealTimeSince LastUsed;
	}

	// Keyed by subject AND every setting that can vary. The same head wanted at two resolutions, or with two
	// different outlines, is genuinely two renders — sharing one panel would just make them overwrite each
	// other's settings every frame. Colour is packed to an int so the key never depends on float equality.
	readonly Dictionary<(object Key, int Size, float Outline, int Ink, bool Silhouette), Entry> _entries = new();

	static (object, int, float, int, bool) CacheKey( in SdfIconRequest request ) =>
		(request.Key, request.RenderSize, request.OutlineWidth ?? -1f, PackColour( request.OutlineColor ), request.Silhouette);

	static int PackColour( Color? colour )
	{
		if ( colour is not { } c )
			return -1; // "use the rig's" — distinct from any real colour

		return ((int)(Math.Clamp( c.r, 0f, 1f ) * 255f) << 16)
			| ((int)(Math.Clamp( c.g, 0f, 1f ) * 255f) << 8)
			| (int)(Math.Clamp( c.b, 0f, 1f ) * 255f);
	}

	public SdfIconFarm()
	{
		Farms.Add( this );
	}

	/// <summary>
	/// The texture for this subject, rendering it if we haven't already. Null until the first render lands —
	/// callers should just try again next frame rather than treating it as failure.
	/// </summary>
	public Texture TextureFor( in SdfIconRequest request )
	{
		if ( request.Key is null || request.RenderSize < 1 )
			return null;

		var id = CacheKey( request );

		if ( !_entries.TryGetValue( id, out var entry ) || !entry.Panel.IsValid() )
		{
			entry = new Entry { Panel = AddChild<SdfThumbnail>() };
			entry.Panel.Style.Width = Length.Pixels( request.RenderSize );
			entry.Panel.Style.Height = Length.Pixels( request.RenderSize );
			_entries[id] = entry;
		}

		// Re-assigning every request is what keeps a live head current: SdfThumbnail hangs its own
		// commit-watching off Source, and no-ops when a value hasn't actually changed.
		entry.Panel.Source = request.Source;
		entry.Panel.Brushes = request.Brushes;
		entry.Panel.OutlineColor = request.OutlineColor;
		entry.Panel.OutlineWidth = request.OutlineWidth;
		entry.Panel.Silhouette = request.Silhouette;
		entry.Panel.Pose = request.Pose;
		entry.Panel.Frozen = request.Frozen;
		entry.LastUsed = 0;

		return entry.Panel.RenderTexture;
	}

	public override void Tick()
	{
		base.Tick();

		if ( _entries.Count == 0 )
			return;

		List<(object, int, float, int, bool)> expired = null;

		foreach ( var (id, entry) in _entries )
		{
			if ( entry.LastUsed < PruneAfter && entry.Panel.IsValid() )
				continue;

			expired ??= new List<(object, int, float, int, bool)>();
			expired.Add( id );
		}

		if ( expired is null )
			return;

		foreach ( var id in expired )
		{
			// Deleting the panel disposes its SdfStage, and with it a SceneWorld — this is the only thing
			// stopping a lobby's worth of departed players leaking one each.
			_entries[id].Panel?.Delete();
			_entries.Remove( id );
		}
	}

	public override void OnDeleted()
	{
		Farms.Remove( this );
		_entries.Clear();

		base.OnDeleted();
	}
}
