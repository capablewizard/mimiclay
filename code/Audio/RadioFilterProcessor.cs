using System;
using Sandbox.Audio;

namespace Mimiclay;

/// <summary>
/// Band-pass "little speaker" filter for a mixer: a high-pass (cuts the bass) and a
/// low-pass (cuts the treble) biquad pair with real Hz cutoffs, plus optional tanh
/// saturation. Put it on a dedicated mixer (e.g. "Radio" under Music) and route the
/// radio's sound event to that mixer — processors run per mixer, not per sound.
/// </summary>
/// <remarks>
/// The engine's HighPassProcessor is a gain in disguise (its recurrence telescopes to
/// y = a·x + c), which is why this exists. Coefficients are RBJ-cookbook biquads run in
/// transposed direct form II. Runs on the mix thread; properties are plain floats set
/// from the main thread, so a tweak lands on the next 512-sample block.
/// </remarks>
[Title( "Radio Filter" )]
[Icon( "radio" )]
public sealed class RadioFilterProcessor : AudioProcessor<RadioFilterProcessor.State>
{
	const int MaxChannels = 8;
	const int MaxStages = 2;

	// AudioEngine.SamplingRate (MIX_DEFAULT_SAMPLING_RATE) — internal to the engine, so mirrored here.
	const float SampleRate = 44100f;

	/// <summary>Frequencies below this are cut (Hz). Raise for a thinner, tinnier speaker.</summary>
	[Range( 20, 2000 )]
	public float LowCut { get; set; } = 300f;

	/// <summary>Frequencies above this are cut (Hz). Lower for a duller, cheaper speaker.</summary>
	[Range( 500, 20000 )]
	public float HighCut { get; set; } = 3500f;

	/// <summary>Resonance at both corners. 0.707 is flat; ~1.5 adds a honky boxy peak.</summary>
	[Range( 0.3f, 4f )]
	public float Resonance { get; set; } = 0.707f;

	/// <summary>Filter stages per side: 1 = 12 dB/oct (soft), 2 = 24 dB/oct (steep, very "radio").</summary>
	[Range( 1, MaxStages )]
	public int Stages { get; set; } = 2;

	/// <summary>Soft-clip saturation. 0 = clean; small amounts add crunch and loudness.</summary>
	[Range( 0, 1 )]
	public float Drive { get; set; } = 0f;

	/// <summary>Output level (dB). Band-passing removes a lot of energy; make it back up here.</summary>
	[Range( -24, 24 )]
	public float OutputGain { get; set; } = 3f;

	public class State : ListenerState
	{
		// [channel][stage][hp/lp][z1,z2] flattened.
		internal readonly float[] Z = new float[MaxChannels * MaxStages * 2 * 2];
	}

	struct Biquad
	{
		public float B0, B1, B2, A1, A2;

		public static Biquad Make( bool highPass, float hz, float q )
		{
			var fs = SampleRate;
			hz = Math.Clamp( hz, 10f, fs * 0.45f );

			var w0 = 2f * MathF.PI * hz / fs;
			var cos = MathF.Cos( w0 );
			var alpha = MathF.Sin( w0 ) / (2f * MathF.Max( q, 0.05f ));
			var a0 = 1f + alpha;

			var b1 = highPass ? -(1f + cos) : 1f - cos;
			var b0 = MathF.Abs( b1 ) * 0.5f;

			return new Biquad
			{
				B0 = b0 / a0,
				B1 = b1 / a0,
				B2 = b0 / a0,
				A1 = -2f * cos / a0,
				A2 = (1f - alpha) / a0,
			};
		}
	}

	protected override void ProcessSingleChannel( AudioChannel channel, Span<float> input )
	{
		var ch = channel.Get();
		if ( ch >= MaxChannels ) return;

		var z = CurrentState.Z;
		var stages = Math.Clamp( Stages, 1, MaxStages );

		// Recomputed per block (a handful of trig ops per 512 samples) so edits apply live.
		var hp = Biquad.Make( true, LowCut, Resonance );
		var lp = Biquad.Make( false, HighCut, Resonance );

		for ( int s = 0; s < stages; s++ )
		{
			var baseIndex = (ch * MaxStages + s) * 4;
			Run( in hp, input, z, baseIndex );
			Run( in lp, input, z, baseIndex + 2 );
		}

		var outGain = MathF.Pow( 10f, OutputGain / 20f );

		if ( Drive > 0.001f )
		{
			// Normalised so a full-scale sample stays full-scale; quieter material gets pushed up into the knee.
			var pre = 1f + Drive * 8f;
			var norm = outGain / MathF.Tanh( pre );
			for ( int i = 0; i < input.Length; i++ )
				input[i] = MathF.Tanh( input[i] * pre ) * norm;
		}
		else if ( MathF.Abs( outGain - 1f ) > 0.0001f )
		{
			for ( int i = 0; i < input.Length; i++ )
				input[i] *= outGain;
		}
	}

	static void Run( in Biquad f, Span<float> buf, float[] z, int zi )
	{
		var z1 = z[zi];
		var z2 = z[zi + 1];

		for ( int i = 0; i < buf.Length; i++ )
		{
			var x = buf[i];
			var y = f.B0 * x + z1;
			z1 = f.B1 * x - f.A1 * y + z2;
			z2 = f.B2 * x - f.A2 * y;
			buf[i] = y;
		}

		// Flush denormals once the tail has decayed (silence after the radio stops).
		if ( MathF.Abs( z1 ) < 1e-20f ) z1 = 0f;
		if ( MathF.Abs( z2 ) < 1e-20f ) z2 = 0f;

		z[zi] = z1;
		z[zi + 1] = z2;
	}
}
