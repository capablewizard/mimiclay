using System;

namespace Mimiclay;

/// <summary>
/// The bits of the test-bot harness that both the lobby (<see cref="LobbyManager"/>) and a round
/// (<see cref="RoundManager"/>) need, kept in one place so a bot is the same thing on either side of the scene
/// change — same id for the same seat, same name, same "nobody is driving this" preparation.
///
/// A bot is a roster row with no machine behind it. That means the HOST holds its body, which is exactly the case
/// the rest of the codebase's ownership shorthand doesn't cover: a host-owned bot pawn is neither a proxy nor the
/// local player's, so every owner-only behaviour on it has to be switched off explicitly — see
/// <see cref="Prepare"/>.
/// </summary>
public static class RoundBots
{
	/// <summary>The roster id for bot seat <paramref name="index"/>: the seat number in the last field of an
	/// otherwise-zero guid. Deterministic (a bot keeps its identity across a re-assign, and across the lobby →
	/// map scene change), sorts in seat order, and can't collide with a real connection's guid.</summary>
	public static Guid IdFor( int index ) => new( $"00000000-0000-0000-0000-{index:D12}" );

	/// <summary>Display name for bot seat <paramref name="index"/> — 1-based, so the first bot reads "Bot 1".</summary>
	public static string NameFor( int index ) => $"Bot {index + 1}";

	/// <summary>Turn a freshly-cloned pawn into a bot's body: stamp the roster row it stands for, take the controls
	/// away from it, and strip the per-player hardware it has no use for. Call before the pawn goes on the wire —
	/// <see cref="BotPawn.RosterId"/> has to be in the spawn snapshot for clients to resolve it.</summary>
	public static void Prepare( GameObject pawn, Guid rosterId )
	{
		if ( !pawn.IsValid() )
			return;

		// Which row this body is. Every machine resolves bot pawns through it (RoundManager.RosterIdOf), because
		// Network.Owner says "the host" for all of them — and for the host's own pawn too.
		pawn.Components.GetOrCreate<BotPawn>().RosterId = rosterId;

		// Nobody at the controls: no input, no camera, no mouse capture. A prop already has that state — it's what
		// a disguise released into the level does — and the hunter grew the same flag for bots. Without it a
		// host-owned pawn is indistinguishable from the host's own and fights it for the one shared camera.
		var hider = pawn.Components.Get<HiderController>();
		if ( hider.IsValid() )
			hider.ReleaseControl();

		var hunter = pawn.Components.Get<HunterController>();
		if ( hunter.IsValid() )
			hunter.Bot = true;

		Strip( pawn );
	}

	// A bot has no screen and no microphone. Both pawn prefabs carry per-player hardware that only makes sense for
	// the person holding the controller, and every piece of it decides "is this mine?" by asking whether the pawn is
	// a proxy — which a host-owned bot is NOT. Left alone, eight bots means eight full-screen HUD overlays stacked
	// on the real player's (the tell: nameplates painted once per overlay look heavier than a real player's, which
	// every overlay skips as its own pawn), eight crosshairs, eight pause menus racing for Escape, and eight voices
	// broadcasting whenever the host holds push-to-talk. Taking the hardware away is both cheaper and more honest
	// than teaching each consumer what a bot is.
	static void Strip( GameObject pawn )
	{
		// The whole HUD branch, not just the panel: its UI components run per-frame scene scans of their own.
		foreach ( var screen in pawn.Components.GetAll<Sandbox.ScreenPanel>( FindMode.EverythingInSelfAndDescendants ) )
			screen.GameObject.Enabled = false;

		// Attached by both controllers in OnAwake — which has already run, since Clone initialises synchronously
		// (RoundManager.SpawnOwnPawn relies on the same thing when it publishes a pawn the instant it clones it).
		foreach ( var voice in pawn.Components.GetAll<PlayerVoice>( FindMode.EverythingInSelfAndDescendants ) )
			voice.Enabled = false;
	}

	/// <summary>Dress a bot in a random shape from this machine's <see cref="SculptLibrary"/>, so a test lobby or
	/// map fills with varied silhouettes instead of a row of identical blobs. False when there's nothing to wear —
	/// no saves, or the sculpture hasn't resolved yet (a hider resolves its disguise in OnStart, so the caller
	/// retries next frame). Setting the brushes and rebuilding is the same move
	/// <see cref="SculptEditSession.Load"/> makes: the <see cref="SdfSculpture.Committed"/> that Rebuild fires is
	/// what re-cuts the collider and recentres the origin, so the bot ends up solid and standing on its feet.</summary>
	public static bool TryWearRandomSculpt( SdfSculpture sculpture )
	{
		if ( !sculpture.IsValid() )
			return false;

		var names = SculptLibrary.List();
		if ( names.Count == 0 )
			return true; // nothing saved — settled, on the prefab's default shape

		var entry = SculptLibrary.Load( names[Random.Shared.Next( names.Count )] );
		if ( entry is null )
			return true;

		sculpture.Brushes = entry.Brushes;
		sculpture.Resolution = entry.Resolution;
		sculpture.FlipFaces = entry.FlipFaces;
		sculpture.Rebuild();
		return true;
	}
}
