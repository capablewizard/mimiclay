using System;
using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// Authoring helper that fills a target <see cref="SdfSculpture"/> with a stylised closed book — a
/// procedural take on book_star.prefab (minus the star). A cream page block, a coloured cover
/// (front + back, mirrored) that overhangs the pages, a rounded cylindrical spine on the +X edge,
/// and 45° chamfers clipping the two fore-edge corners.
///
/// Local frame: X = fore-edge (−X) → spine (+X), Y = height, Z = cover-to-cover thickness, so the
/// big cover faces look up/down (±Z).
///
/// Add it next to a sculpture, tweak the sliders live, then "Apply &amp; Remove Builder" to bake the
/// brushes and delete the builder. It OWNS the sculpture's brush list while attached.
/// </summary>
[Title( "Book Builder" )]
[Category( "SDF" )]
[Icon( "menu_book" )]
public sealed class BookBuilder : Component, Component.ExecuteInEditor
{
	/// <summary>The sculpture to fill. Defaults to one on this GameObject.</summary>
	[Property] public SdfSculpture Sculpture { get; set; }

	[Property, Range( 6f, 120f ), Group( "Book" )] public float Width { get; set; } = 34f;      // X: fore-edge→spine
	[Property, Range( 6f, 120f ), Group( "Book" )] public float Height { get; set; } = 42f;      // Y
	[Property, Range( 4f, 60f ), Group( "Book" )] public float Thickness { get; set; } = 17f;    // Z: cover→cover

	[Property, Group( "Cover" )] public bool ShowCovers { get; set; } = true;
	[Property, Group( "Cover" )] public Color CoverColor { get; set; } = new( 0.2f, 0.5f, 0.85f );
	/// <summary>Thickness of each (front/back) cover slab.</summary>
	[Property, Range( 0.5f, 8f ), Group( "Cover" )] public float CoverThickness { get; set; } = 4.4f;
	/// <summary>How far the covers overhang the page block on the open edges.</summary>
	[Property, Range( 0f, 6f ), Group( "Cover" )] public float CoverOverhang { get; set; } = 1.5f;
	/// <summary>Pulls the covers back from the spine. The covers already reach the cut-back spine by
	/// default (no gap); raise this to tuck them back in if they poke past the spine's bulge.</summary>
	[Property, Range( 0f, 15f ), Group( "Cover" )] public float CoverOffset { get; set; } = 0f;
	/// <summary>Corner/edge rounding of the cover slabs (softness).</summary>
	[Property, Range( 0f, 6f ), Group( "Cover" )] public float CoverRounding { get; set; } = 1.5f;
	/// <summary>Smooth-blend of the covers into the rest of the book (0 = crisp, as the original).</summary>
	[Property, Range( 0f, 8f ), Group( "Cover" )] public float CoverBlend { get; set; } = 0f;

	[Property, Group( "Pages" )] public bool ShowPages { get; set; } = true;
	[Property, Group( "Pages" )] public Color PageColor { get; set; } = new( 0.95f, 0.9f, 0.75f );
	[Property, Range( 0f, 6f ), Group( "Pages" )] public float PageRounding { get; set; } = 2.5f;
	/// <summary>Smooth-blend of the page block into the rest of the book (0 = crisp, as the original).</summary>
	[Property, Range( 0f, 8f ), Group( "Pages" )] public float PageBlend { get; set; } = 0f;
	/// <summary>Extends the page block toward the spine (+X) by this much, so the pages reach into the
	/// spine instead of stopping at the book edge. The fore-edge stays put.</summary>
	[Property, Range( 0f, 15f ), Group( "Pages" )] public float PageOffset { get; set; } = 0f;

	[Property, Group( "Spine" )] public bool ShowSpine { get; set; } = true;
	/// <summary>Radius of the rounded cylindrical spine on the +X binding edge. ~half the thickness
	/// gives a semicircular spine; 0 removes it (leaving a square bound edge).</summary>
	[Property, Range( 0f, 30f ), Group( "Spine" )] public float SpineRadius { get; set; } = 8.5f;
	/// <summary>How much the spine melts into the covers/pages.</summary>
	[Property, Range( 0f, 4f ), Group( "Spine" )] public float SpineBlend { get; set; } = 1f;

	/// <summary>Radius of the cylinder carved OUT of the spine to hollow it into a curved shell. 0 =
	/// solid spine. Must be smaller than SpineRadius.</summary>
	[Property, Range( 0f, 25f ), Group( "Spine" )] public float SpineHollow { get; set; } = 5f;
	/// <summary>How far inward (toward the pages) the hollow cylinder is offset, so the shell is
	/// thicker on the outer face and opens toward the pages.</summary>
	[Property, Range( 0f, 8f ), Group( "Spine" )] public float SpineHollowOffset { get; set; } = 0.5f;
	[Property, Range( 0f, 4f ), Group( "Spine" )] public float SpineHollowBlend { get; set; } = 0.9f;

	/// <summary>Flattens the spine bulge: a box that slices it back from the outer edge. 0 = full
	/// round bulge, higher shaves it toward flat.</summary>
	[Property, Range( 0f, 20f ), Group( "Spine" )] public float SpineCut { get; set; } = 0f;
	/// <summary>Softness of that slice — a bigger value rounds the groove where the spine meets the
	/// covers (the binding groove).</summary>
	[Property, Range( 0f, 4f ), Group( "Spine" )] public float SpineCutBlend { get; set; } = 1.5f;

	/// <summary>Length of the 45° chamfer clipping each fore-edge corner. 0 leaves square corners.</summary>
	[Property, Range( 0f, 20f ), Group( "Corners" )] public float CornerBevel { get; set; } = 8f;
	[Property, Range( 0f, 4f ), Group( "Corners" )] public float BevelBlend { get; set; } = 1f;

	int _lastHash;

	protected override void OnEnabled()
	{
		Sculpture ??= GameObject.Components.Get<SdfSculpture>();
		Regenerate();
	}

	protected override void OnUpdate()
	{
		if ( ParamHash() != _lastHash )
			Regenerate();
	}

	[Button( "Rebuild Now" )]
	public void Regenerate()
	{
		Sculpture ??= GameObject.Components.Get<SdfSculpture>();
		if ( !Sculpture.IsValid() )
		{
			Log.Warning( "Book Builder: no SdfSculpture assigned (or on this object)." );
			return;
		}

		Sculpture.Brushes = BuildBrushes();
		Sculpture.Rebuild();
		_lastHash = ParamHash();
	}

	/// <summary>Bake the current book into the sculpture and delete this builder — the brushes stay.</summary>
	[Button( "Apply & Remove Builder" )]
	public void ApplyAndRemove()
	{
		Regenerate();
		Destroy();
	}

	int ParamHash()
	{
		var h = new HashCode();
		h.Add( Width ); h.Add( Height ); h.Add( Thickness );
		h.Add( ShowSpine ); h.Add( ShowCovers ); h.Add( ShowPages );
		h.Add( CoverColor ); h.Add( CoverThickness ); h.Add( CoverOverhang ); h.Add( CoverOffset );
		h.Add( CoverRounding ); h.Add( CoverBlend );
		h.Add( PageColor ); h.Add( PageRounding ); h.Add( PageBlend ); h.Add( PageOffset );
		h.Add( SpineRadius ); h.Add( SpineBlend );
		h.Add( SpineHollow ); h.Add( SpineHollowOffset ); h.Add( SpineHollowBlend );
		h.Add( SpineCut ); h.Add( SpineCutBlend );
		h.Add( CornerBevel ); h.Add( BevelBlend );
		h.Add( Sculpture );
		return h.ToHashCode();
	}

	List<SdfBrush> BuildBrushes()
	{
		float hw = Width * 0.5f, hh = Height * 0.5f, ht = Thickness * 0.5f;
		float ct = MathF.Min( CoverThickness, 2f * ht - 0.5f ); // one cover slab, full thickness
		float m = CoverOverhang;

		var list = new List<SdfBrush>( 5 );

		// Build order: spine → covers → pages (then the fore-edge chamfer). Spine is FIRST so its
		// hollow/cut subtracts only carve the spine, not the parts added afterwards.
		bool haveSpine = ShowSpine && SpineRadius > 0.05f;
		if ( haveSpine )
		{
			var spineRot = Rotation.FromAxis( new Vector3( 1f, 0f, 0f ), -90f ); // cylinder axis Z → Y
			list.Add( new SdfBrush
			{
				Shape = SdfShape.Cylinder,
				Position = new Vector3( hw, 0f, 0f ),
				Rotation = spineRot,
				Size = new Vector3( SpineRadius, SpineRadius, hh ),  // radius, (unused), half-height
				Rounding = 0.75f,
				Blend = SpineBlend,
				Color = CoverColor,
				Roughness = 0.5f,
			} );

			// Hollow: a smaller cylinder subtracted from the spine, offset inward toward the pages, so
			// the spine becomes a curved shell — thicker on the outer face, opening toward the pages.
			if ( SpineHollow > 0.05f )
			{
				list.Add( new SdfBrush
				{
					Shape = SdfShape.Cylinder,
					Operation = SdfOperation.Subtract,
					Position = new Vector3( hw - SpineHollowOffset, 0f, 0f ),
					Rotation = spineRot,
					Size = new Vector3( MathF.Min( SpineHollow, SpineRadius - 0.25f ), 1f, hh + SpineRadius ),
					Rounding = 0.75f,
					Blend = SpineHollowBlend,
					Color = CoverColor,
					Roughness = 0.5f,
				} );
			}

			// Cut: a box that trims the spine's inner half (and, with SpineCut, shaves the bulge). Its
			// +X face sits at hw + SpineCut; the smooth subtract rounds the binding groove. Sized to just
			// cover the spine (span the radius/height/cut plus a blend margin) rather than the whole
			// scene, and still before the pages so it only carves the spine.
			float pad = SpineCutBlend + 2f;
			float cutHalfX = (SpineRadius + SpineCut + pad) * 0.5f;
			list.Add( new SdfBrush
			{
				Shape = SdfShape.Box,
				Operation = SdfOperation.Subtract,
				Position = new Vector3( hw + SpineCut - cutHalfX, 0f, 0f ),
				Size = new Vector3( cutHalfX, hh + pad, SpineRadius + pad ),
				Rounding = 0.75f,
				Blend = SpineCutBlend,
				Color = CoverColor,
				Roughness = 0.5f,
			} );
		}

		// Covers (front + back) — thin slabs on ±Z overhanging the pages. MirrorZ makes the pair. The
		// +X (spine-side) edge reaches the cut-back spine (SpineCut + its blend erosion) so there's no
		// gap; CoverOffset pulls it back the other way. The −X (fore-edge) edge stays put.
		if ( ShowCovers )
		{
			float coverExtend = (haveSpine ? SpineCut + SpineCutBlend : 0f) - CoverOffset;
			list.Add( new SdfBrush
			{
				Shape = SdfShape.Box,
				Position = new Vector3( coverExtend * 0.5f, 0f, ht - ct * 0.5f ),
				Size = new Vector3( hw + coverExtend * 0.5f, hh, ct * 0.5f ),
				Rounding = CoverRounding,
				Blend = CoverBlend,
				Color = CoverColor,
				Roughness = 0.5f,
				MirrorZ = true,
			} );
		}

		// Page block (cream) — fills between the covers' inner faces; inset from the fore-edge (−X) and
		// top/bottom (Y) so the cover overhangs and the cream shows, but flush at the spine (+X).
		if ( ShowPages )
		{
			list.Add( new SdfBrush
			{
				Shape = SdfShape.Box,
				Position = new Vector3( (m + PageOffset) * 0.5f, 0f, 0f ),
				Size = new Vector3( MathF.Max( hw - m * 0.5f + PageOffset * 0.5f, 1f ), MathF.Max( hh - m, 1f ), MathF.Max( ht - ct, 1f ) ),
				Rounding = PageRounding,
				Blend = PageBlend,
				Color = PageColor,
				Roughness = 0.5f,
			} );
		}

		// Fore-edge corner chamfers — a 45°-rotated box subtract whose inner face lies on the chamfer
		// line at the top-fore corner (−hw, +hh); MirrorY does the bottom-fore corner too. Last, so it
		// clips whatever covers/pages are present.
		if ( ShowCovers && CornerBevel > 0.05f )
		{
			var outward = new Vector3( -1f, 1f, 0f ).Normal; // book → corner direction (XY)
			var corner = new Vector3( -hw, hh, 0f );
			float bevPad = BevelBlend + 1.5f;
			float bevReach = CornerBevel * 0.70710678f;      // corner distance from the chamfer line
			float bevHalf = bevReach + bevPad;               // just cover the corner triangle + margin
			var center = corner + outward * bevPad;          // −Y face lands on the chamfer line
			list.Add( new SdfBrush
			{
				Shape = SdfShape.Box,
				Operation = SdfOperation.Subtract,
				Position = center,
				Rotation = Rotation.FromAxis( new Vector3( 0f, 0f, 1f ), 45f ),
				Size = new Vector3( bevHalf, bevHalf, ht + 2f ),
				Rounding = 0.75f,
				Blend = BevelBlend,
				MirrorY = true,
				Color = CoverColor,
				Roughness = 0.5f,
			} );
		}

		return list;
	}
}
