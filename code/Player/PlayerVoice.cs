namespace Mimiclay;

/// <summary>
/// The pawn's voice chat — the stock engine <see cref="Voice"/> with this game's defaults baked in. The engine
/// component does all the real work (mic capture, push-to-talk input polling, owner-only broadcast RPC,
/// spatialized playback on proxies); this subclass exists so both pawn controllers attach ONE consistently
/// configured thing, and so future team/phase hearing rules have a home (<c>ExcludeFilter</c> /
/// <c>ShouldHearVoice</c> overrides).
///
/// Push-to-talk reads the <c>Voice</c> input action (V, already in Input.config) — but the player's own s&amp;box
/// voice preference (<c>voip_mode</c>) has the final say: open-mic users transmit without the key, disabled
/// users never transmit, regardless of what we set here.
///
/// Playback is 3D at the pawn (<see cref="Voice.WorldspacePlayback"/>): a hiding prop that talks is audible to
/// a nearby hunter — that's the intended risk. A concealed infection-prep pawn isn't on the wire at all, so its
/// voice reaches nobody until Hunt networks it.
/// </summary>
[Title( "Player Voice" )]
[Category( "Mimiclay" )]
[Icon( "record_voice_over" )]
public sealed class PlayerVoice : Voice
{
	/// <summary>Seconds since the last decoded voice frame under which a player counts as "speaking". Meaningful
	/// on any machine (playback-driven), unlike IsRecording/IsListening which only report for the owner.</summary>
	const float SpeakingWindow = 0.25f;

	/// <summary>True while this player's voice is actually coming out of the speakers — drives the nameplate icon.</summary>
	public bool IsSpeaking => LastPlayed < SpeakingWindow;

	/// <summary>Per-machine mute for a body nobody is driving. The engine gates recording on !IsProxy alone —
	/// and an UNOWNED pawn reads !IsProxy on the HOST, so every released prop would otherwise open the host's
	/// mic through itself (and race the host's real pawn for the engine's single recorder slot). Set true on
	/// release (<see cref="HiderController.ReleaseControl"/>), false on possession
	/// (<see cref="HiderController.ResumeControl"/>).
	///
	/// The engine seals Voice.OnUpdate and IsListening isn't virtual, but IsListening reads two plain local
	/// properties we can starve instead — nothing here is [Sync]'d, so the write only ever affects the machine
	/// that makes it, which is exactly the machine that could wrongly record:
	///   • open-mic voip_mode is gated by Mode — Manual only listens when IsListening is set (we never set it);
	///   • push-to-talk voip_mode is gated by the input action alone (Mode is ignored on that branch!) — an
	///     empty action name is Input.Down == false, always.
	/// Playback of OTHER people's voice through this component consults neither property, so a muted copy still
	/// hears everything.</summary>
	public bool Muted
	{
		get => Mode == ActivateMode.Manual; // Manual is only ever set by the mute — see the setter
		set
		{
			Mode = value ? ActivateMode.Manual : ActivateMode.PushToTalk;
			PushToTalkInput = value ? "" : "voice";
		}
	}

	// Constructor, not OnAwake: these are DEFAULTS. Deserialization runs after construction, so a prefab or
	// inspector tweak (or the owner's snapshot state on a proxy) still wins over them.
	public PlayerVoice()
	{
		Mode = ActivateMode.PushToTalk; // open-mic users still get open mic — their voip_mode preference wins
		LipSync = false;                // pawns are SDF sculpts, there's no SkinnedModelRenderer to morph
		WorldspacePlayback = true;
		Distance = 4000f;               // moderate reach so proximity matters at room scale (engine default is map-wide)
	}
}
