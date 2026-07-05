using System;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// One preset clay material — a colour plus PBR metalness/roughness. The unit a player picks from the
/// palette, like one tub in a Play-Doh kit.
/// </summary>
public class ClayMaterial
{
	[KeyProperty] public string Name { get; set; } = "Clay";
	[Property] public Color Color { get; set; } = Color.White;
	[Property, Range( 0f, 1f )] public float Metalness { get; set; } = 0f;
	[Property, Range( 0f, 1f )] public float Roughness { get; set; } = 1f;

	/// <summary>Copy this material's look onto a brush.</summary>
	public void ApplyTo( SdfBrush brush )
	{
		if ( brush is null )
			return;

		brush.Color = Color;
		brush.Metallic = Metalness;
		brush.Roughness = Roughness;
	}

	/// <summary>True if a brush currently looks like this material (used to highlight the active swatch).</summary>
	public bool Matches( SdfBrush b )
	{
		static bool Near( float a, float c ) => MathF.Abs( a - c ) < 0.02f;
		return b is not null
			&& Near( b.Color.r, Color.r ) && Near( b.Color.g, Color.g ) && Near( b.Color.b, Color.b )
			&& Near( b.Metallic, Metalness ) && Near( b.Roughness, Roughness );
	}
}

/// <summary>
/// A global, editable kit of clay materials. Create one or more <c>.clay</c> assets in the editor (they
/// get an auto-generated inspector); the edit HUD shows them as swatches. Access from anywhere via
/// <see cref="Default"/>. Data-driven — change the colours in the asset, no recompile.
/// </summary>
[AssetType( Name = "Clay Palette", Extension = "clay", Category = "Mimiclay" )]
public class ClayPalette : GameResource
{
	[Property] public List<ClayMaterial> Materials { get; set; } = new();

	static readonly ClayPalette _empty = new(); // null-safety only (no colours) — palettes live in .clay assets

	/// <summary>The palette used everywhere: the first non-empty <c>.clay</c> asset, falling back to loading
	/// the starter palette by path (<c>GetAll</c> only returns resources already loaded, so a path load
	/// forces it in if nothing referenced it yet), then an empty palette as a last resort.</summary>
	public static ClayPalette Default
	{
		get
		{
			var found = ResourceLibrary.GetAll<ClayPalette>().FirstOrDefault( p => p.Materials is { Count: > 0 } )
				?? ResourceLibrary.Get<ClayPalette>( "clay/starter_colours.clay" );
			return found ?? _empty;
		}
	}
}
