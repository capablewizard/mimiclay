using System;
using System.Collections.Generic;
using System.IO;
using Sandbox;

namespace Editor;

/// <summary>
/// Keeps loaded <see cref="Mimiclay.SdfQualityProfile"/> instances in step with their .sdfq files on disk.
///
/// Inspector edits already apply live: the asset inspector edits the same instance the renderers read every frame
/// (SdfRaymarchRenderer.ResolveQuality), and a density change re-bakes through the field hash. But the engine does NOT
/// reload a GameResource's live instance when its file changes underneath it — a text-editor edit, a git checkout,
/// MCP asset_write and asset_compile all left the old values running (2026-10-25). This polls each loaded profile's
/// source file and deserializes it back into the live instance when it changes, so those edits show up within a
/// second too. A profile with unsaved inspector edits is left alone (the disk copy would discard them).
/// </summary>
public static class SdfQualityProfileWatch
{
	static readonly Dictionary<Mimiclay.SdfQualityProfile, DateTime> _seen = new();
	static RealTimeSince _sinceCheck;

	[EditorEvent.Frame]
	public static void Tick()
	{
		if ( _sinceCheck < 0.5f )
			return;
		_sinceCheck = 0;

		foreach ( var profile in ResourceLibrary.GetAll<Mimiclay.SdfQualityProfile>() )
		{
			var file = AssetSystem.FindByPath( profile.ResourcePath )?.AbsolutePath;
			if ( string.IsNullOrEmpty( file ) || !File.Exists( file ) )
				continue;

			var stamp = File.GetLastWriteTimeUtc( file );
			if ( !_seen.TryGetValue( profile, out var last ) )
			{
				_seen[profile] = stamp; // first sight: the instance was loaded from this file
				continue;
			}
			if ( stamp == last )
				continue;
			_seen[profile] = stamp;

			if ( profile.HasUnsavedChanges )
			{
				Log.Warning( $"[sdf] {profile.ResourcePath} changed on disk but has unsaved inspector edits — not reloaded" );
				continue;
			}

			try
			{
				if ( System.Text.Json.Nodes.JsonNode.Parse( File.ReadAllText( file ) ) is System.Text.Json.Nodes.JsonObject json )
				{
					profile.Deserialize( json );
					Log.Info( $"[sdf] reloaded quality profile {profile.ResourcePath} from disk" );
				}
			}
			catch ( Exception e )
			{
				Log.Warning( $"[sdf] couldn't reload {profile.ResourcePath}: {e.Message}" );
			}
		}
	}
}
