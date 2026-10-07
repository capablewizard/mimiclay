using System;

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

	/// <summary>How long a roster 🔊 badge holds after the last voice frame. Packets are bursty and voice
	/// activation goes quiet between words, so the raw <see cref="SpeakingWindow"/> would flicker a badge
	/// mid-sentence.</summary>
	public const float BadgeHold = 0.6f;

	/// <summary>Roster ids of everyone whose voice played within <paramref name="hold"/> seconds, on this machine.
	/// LastPlayed, NOT IsRecording: the engine's Msg_Voice broadcast also runs on the sender (decoded locally, muted
	/// only at the mixer), so it ticks for our own pawn exactly as for a proxy — and only while audio is actually
	/// sent, where IsRecording sits true the whole time for an open-mic user. Keyed by RosterIdOf, the bot-safe
	/// resolver (a released prop on the host reads !IsProxy but answers as nobody's).</summary>
	public static HashSet<Guid> Speakers( Scene scene, float hold = BadgeHold )
	{
		var set = new HashSet<Guid>();
		foreach ( var v in scene.GetAllComponents<PlayerVoice>() )
			if ( v.IsValid() && v.LastPlayed < hold && RoundManager.RosterIdOf( v.GameObject ) is { } id )
				set.Add( id );
		return set;
	}

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
		LipSync = true;                 // the engine's OVR analysis fills Visemes per decoded frame (no renderer needed
		                                // since the user's engine change); the SDF mouth reads them via CurrentViseme
		WorldspacePlayback = true;
		Distance = 4000f;               // moderate reach so proximity matters at room scale (engine default is map-wide)
	}

	// ── lip-sync gate workaround ────────────────────────────────────────────────────────────────────────
	// The current engine only runs the OVR viseme analysis when the voice has a SkinnedModelRenderer
	// (`sound.LipSync.Enabled = LipSync && Renderer.IsValid()`, set when the first voice packet arrives).
	// Our heads are SDF sculpts, so give it a dummy: a DISABLED, model-less renderer on a local-only child.
	// Disabled = no scene object (an enabled null-model renderer draws the dev box) but still IsValid, and
	// the engine's morph pass bails on a null model, so nothing is ever drawn or animated. Created on every
	// machine for its own copy of the pawn (proxies need the analysis too) — never saved or networked.
	// Harmless once Facepunch/sbox-public f5e05ee ships (reading Visemes then enables the analysis itself);
	// delete this block after that build lands.
	protected override void OnStart()
	{
		base.OnStart();
		EnsureLipSyncGate();
	}

	void EnsureLipSyncGate()
	{
		if ( Renderer.IsValid() )
			return;

		var go = new GameObject( false, "LipSyncGate" );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.Hidden;
		go.NetworkMode = NetworkMode.Never;
		go.SetParent( GameObject, false );
		var r = go.Components.Create<SkinnedModelRenderer>( startEnabled: false );
		go.Enabled = true; // the object is live; the renderer component itself stays disabled
		Renderer = r;
	}

	// ── viseme picking (the sculpted mouth's lip-sync) ─────────────────────────────────────────────────────

	/// <summary>Shortest time a chosen mouth shape stays up. The analysis runs per audio frame and flips
	/// between near-equal shapes; clay mouths read better holding a pose than fluttering.</summary>
	public const float VisemeHold = 0.07f;

	/// <summary>A new shape has to beat the current one's live weight by this much to take over (hysteresis).</summary>
	public const float VisemeSwitchMargin = 0.12f;

	/// <summary>Below this weight no shape is confident enough — the mouth shows the plain talking look.</summary>
	public const float VisemeFloor = 0.12f;

	SdfViseme _viseme = SdfViseme.Silence;
	RealTimeSince _sinceSwitch;
	RealTimeSince _sinceWeights = 999f;

	/// <summary>The mouth shape this player's voice is making right now, chosen from the engine's 15 viseme
	/// weights with a hold time and a switch margin so it doesn't flutter. Works on every machine (the weights
	/// come from local playback, our own pawn included). Returns NULL when the engine isn't producing weights
	/// — lip-sync off or unsupported — so the caller can fall back to a plain open/closed toggle; returns
	/// <see cref="SdfViseme.Silence"/> when nothing is confident enough or the player isn't speaking.
	/// Call once per frame (it advances the hysteresis state).</summary>
	public SdfViseme? CurrentViseme()
	{
		var r = CurrentVisemeCore( out string why );
		LastVisemeDebug = why;
		return r;
	}

	/// <summary>Why the last <see cref="CurrentViseme"/> call answered what it did (mimi_dbg_visemes).</summary>
	public string LastVisemeDebug { get; private set; }

	SdfViseme? CurrentVisemeCore( out string why )
	{
		var w = Visemes;
		int n = w?.Count ?? 0;
		string gate = $"lipsync={LipSync} renderer={(Renderer.IsValid() ? "ok" : "none")} weights={n} amp={Amplitude:0.000} laugh={LaughterScore:0.00} loopback={Loopback} proxy={IsProxy}";

		if ( !IsSpeaking )
		{
			_viseme = SdfViseme.Silence;
			why = $"quiet (lastPlayed {LastPlayed.Relative:0.00}s) {gate}";
			return _sinceWeights < 1f ? SdfViseme.Silence : null;
		}

		if ( w is null || n < 15 )
		{
			why = $"SPEAKING but no viseme weights → plain talking shape. {gate}";
			return null;
		}

		// Best non-silence shape this frame.
		int best = 0;
		float bestW = 0f;
		float sum = 0f;
		for ( int i = 1; i < 15; i++ )
		{
			sum += w[i];
			if ( w[i] > bestW ) { bestW = w[i]; best = i; }
		}

		// top three for the log
		var order = new List<int>(); for ( int i = 0; i < 15; i++ ) order.Add( i );
		order.Sort( ( a, b ) => w[b].CompareTo( w[a] ) );
		string top = string.Join( " ", order.GetRange( 0, 3 ).ConvertAll( i => $"{(SdfViseme)i}={w[i]:0.00}" ) );

		if ( sum > 0.001f )
			_sinceWeights = 0f;
		else if ( _sinceWeights > 0.5f )
		{
			why = $"SPEAKING but all weights are 0 for {_sinceWeights.Relative:0.0}s → plain talking shape. {gate}";
			return null; // speaking, but the analysis is giving us nothing — not wired up on this build
		}

		var cur = _viseme;
		float curW = cur == SdfViseme.Silence ? 0f : w[(int)cur];
		var want = bestW < VisemeFloor ? SdfViseme.Silence : (SdfViseme)best;

		if ( want != cur && _sinceSwitch >= VisemeHold )
		{
			// Take over on a clear win, or when the current shape has faded under the floor.
			if ( want == SdfViseme.Silence || cur == SdfViseme.Silence || bestW > curW + VisemeSwitchMargin || curW < VisemeFloor )
			{
				_viseme = want;
				_sinceSwitch = 0f;
			}
		}

		why = $"speaking: top [{top}] sum={sum:0.00} want={want} → picked {_viseme}";
		return _viseme;
	}

	// ── debug ──────────────────────────────────────────────────────────────────────────────────────────
	/// <summary>Toggled by <c>mimi_dbg_visemes</c>: the mouth driver logs what each pawn's voice reports.</summary>
	public static bool DebugVisemes { get; private set; }

	/// <summary>Diagnostic: hear your OWN voice (Voice.Loopback) so the engine mixes it instead of skipping it —
	/// if viseme weights only appear with this on, the analysis is starved of our own muted stream.</summary>
	[ConCmd( "mimi_dbg_voice_loopback" )]
	static void DebugLoopbackCmd()
	{
		var scene = Game.ActiveScene;
		bool on = false;
		foreach ( var v in scene.GetAllComponents<PlayerVoice>() )
			if ( !v.IsProxy ) { v.Loopback = !v.Loopback; on = v.Loopback; }
		Log.Info( $"mimi_dbg_voice_loopback: own voice playback {(on ? "ON (you'll hear yourself)" : "off")}" );
	}

	[ConCmd( "mimi_dbg_visemes" )]
	static void DebugVisemesCmd()
	{
		DebugVisemes = !DebugVisemes;
		Log.Info( $"mimi_dbg_visemes: {(DebugVisemes ? "ON — logs ~5x/s per pawn while it has voice" : "off")}" );
	}
}
