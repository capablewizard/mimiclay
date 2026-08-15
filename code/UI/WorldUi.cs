namespace Mimiclay;

/// <summary>
/// Central read of the world-panel pointer (the lobby pad). True while the local aim ray is over an interactive
/// element of ANY world panel within its interaction range — the engine's <see cref="WorldInput"/> component
/// (on the scene camera) does the ray casting; this just surfaces its hover state as one game-wide flag.
///
/// Two things key off it, and both must agree with what the player sees: the crosshair dot hides (the pointer
/// has "moved onto the screen" — the pad draws its own cursor at the same ray point), and the hunter's trigger
/// is suppressed (WorldInput does NOT consume the click action, so without the gate a click on the screen would
/// also fire the gun).
/// </summary>
public static class WorldUi
{
	public static bool Hovering
	{
		get
		{
			var scene = Game.ActiveScene;
			if ( scene is null )
				return false;

			foreach ( var input in scene.GetAllComponents<WorldInput>() )
			{
				if ( input.Hovered is not null )
					return true;
			}

			return false;
		}
	}
}
