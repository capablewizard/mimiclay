using Sandbox;

namespace Mimiclay;

/// <summary>
/// A selectable prop-hunt map, authored as an asset in the editor (Library → right-click → create "Prop Hunt Map").
/// Each one pairs a <see cref="Scene"/> to load with a <see cref="Preview"/> image for the lobby's map picker.
/// Mirrors MiniMotors' MapResource. The catalogue (<see cref="MapCatalog"/>) enumerates every one of these via
/// <see cref="ResourceLibrary"/>, and a map is referenced everywhere by its <c>ResourcePath</c> (the .phmap path).
/// </summary>
[AssetType( Name = "Prop Hunt Map", Extension = "phmap" )]
public sealed class MapResource : GameResource
{
	/// <summary>Display name on the map card.</summary>
	public string Title { get; set; } = "Untitled";

	/// <summary>Short flavour text (optional) shown under the title.</summary>
	public string Description { get; set; } = "";

	/// <summary>Preview image shown in the setup screen's map panel.</summary>
	public Texture Preview { get; set; }

	/// <summary>The scene loaded when this map is launched.</summary>
	public SceneFile Scene { get; set; }

	/// <summary>Keep this map out of the picker (still openable directly in the editor).</summary>
	public bool Hidden { get; set; } = false;

	// ── Which games this map can host ─────────────────────────────────────────────────────────────────────
	// Per-game bools rather than a list so every EXISTING .phmap asset (which predates these keys) reads the
	// right answer from the defaults: all of them were authored as prop-hunt/creative maps, and none has a
	// charades stage. The setup dialog's map panel filters through MapCatalog.For( game ).

	/// <summary>Selectable when the lobby is set up for Prop Hunt.</summary>
	public bool ForPropHunt { get; set; } = true;

	/// <summary>Selectable when the lobby is set up for Creative.</summary>
	public bool ForCreative { get; set; } = true;

	/// <summary>Selectable when the lobby is set up for Charades. Only maps with a <see cref="CharadesStage"/>
	/// prefab placed in the scene qualify — the game needs somewhere to put the canvas.</summary>
	public bool ForCharades { get; set; } = false;

	/// <summary>Whether this map can host <paramref name="game"/>.</summary>
	public bool Supports( GameModeKind game ) => game switch
	{
		GameModeKind.PropHunt => ForPropHunt,
		GameModeKind.Creative => ForCreative,
		GameModeKind.Charades => ForCharades,
		_ => false,
	};
}
