using System.Collections.Generic;

namespace Mimiclay;

/// <summary>
/// A charades word pool, authored as an asset in the editor (Library → right-click → create "Charades Word
/// List") — the same asset-not-code arrangement as <see cref="MapResource"/>. Each list belongs to one
/// <see cref="CharadesTopics"/> chip; several lists can feed the same topic, so extra packs stack onto the
/// built-ins instead of replacing them. <see cref="CharadesWords"/> unions every non-hidden list whose topic
/// is in the lobby's selection.
///
/// Curation rule of thumb: things you can plausibly SCULPT in soft clay in ~2 minutes — chunky silhouettes,
/// no abstract nouns. Guess matching is case/whitespace-insensitive (<see cref="CharadesWords.Normalize"/>),
/// so "Hot Dog" and "hot dog" are the same word — author whichever reads better on the reveal.
/// </summary>
[AssetType( Name = "Charades Word List", Extension = "words" )]
public sealed class CharadesWordList : GameResource
{
	/// <summary>Display name for the list (asset browsing only — players never see it).</summary>
	public string Title { get; set; } = "Untitled";

	/// <summary>Which topic chip includes this list. One topic per list — split a mixed pack into one asset
	/// per topic rather than tagging multiple.</summary>
	public CharadesTopics Topic { get; set; } = CharadesTopics.Objects;

	/// <summary>Keep this list out of every game without deleting the asset.</summary>
	public bool Hidden { get; set; } = false;

	/// <summary>The words. Spaces are fine ("ice cream"); blanks are skipped.</summary>
	public List<string> Words { get; set; } = new();
}
