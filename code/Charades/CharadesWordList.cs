using System.Collections.Generic;
using System.ComponentModel;

namespace Mimiclay;

/// <summary>
/// A built-in charades phrase list, authored as an asset in the editor (Library → right-click → create
/// "Charades Word List") — the same asset-not-code arrangement as <see cref="MapResource"/>. Each list names
/// the TOPIC it belongs to by its title; the lobby's topic chips are generated from whatever topics the shipped lists
/// declare (see <see cref="CharadesWords.BuiltInTopics"/>), so a new category is a new asset, not a code
/// change. Several lists can share a topic, so extra packs stack onto the built-ins instead of replacing them.
///
/// Community lists are NOT assets — they're plain newline-separated text published through the Steam
/// Workshop (<see cref="CharadesWorkshop"/>) and joined to the pool by the host at setup time.
///
/// Curation rule of thumb: things you can plausibly SCULPT in soft clay in ~2 minutes — chunky silhouettes,
/// no abstract nouns. Phrases are fine ("mario sunbathing on the beach"). Guess matching is case/whitespace/
/// punctuation-insensitive (<see cref="CharadesWords.Normalize"/>) with a little typo slack
/// (<see cref="CharadesWords.Matches"/>), so author whichever spelling reads best on the reveal.
/// </summary>
[AssetType( Name = "Charades Word List", Extension = "words" )]
public sealed class CharadesWordList : GameResource
{
	/// <summary>The topic this list feeds — the chip players see in the lobby ("Animals"). Lists with the same
	/// title (case and spacing ignored) share one chip, so an extra pack stacks onto an existing topic by
	/// using its title. One topic per list — split a mixed pack into one asset per topic.</summary>
	public string Title { get; set; } = "Untitled";

	/// <summary>The topic name, trimmed. (There used to be a separate Topic field; it was folded into the title
	/// after a hotload wiped it on every loaded list and collapsed all six built-in chips into one.)</summary>
	public string TopicName => (Title ?? "").Trim();

	/// <summary>Keep this list out of every game without deleting the asset.</summary>
	public bool Hidden { get; set; } = false;

	/// <summary>The phrases. Spaces are fine ("ice cream"); blanks are skipped. A bracket at the end is a hint
	/// only the sculptor sees — "Steve (Minecraft)" asks for "Steve".</summary>
	[Description( "One phrase per entry. Context in brackets is shown to the sculptor only - e.g. Rust (Video Game)" )]
	public List<string> Words { get; set; } = new();
}
