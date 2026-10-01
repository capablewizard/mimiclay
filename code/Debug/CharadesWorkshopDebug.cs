using System;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Debug: why isn't a published phrase list showing in the lobby's Workshop browser? Runs every browser tab's
/// query plus a direct by-id lookup and logs what Steam hands back (counts, tags, keyvalues, visibility).
/// Usage: <c>mimi_dbg_phrases [fileId]</c>.
/// </summary>
public static class CharadesWorkshopDebug
{
	[ConCmd( "mimi_dbg_phrases" )]
	public static async void Run( string fileId = "" )
	{
		for ( var i = 0; i < CharadesWorkshop.Sorts.Length; i++ )
		{
			try
			{
				var items = await CharadesWorkshop.Browse( i );
				Log.Info( $"[phrases dbg] {CharadesWorkshop.Sorts[i]}: {items.Count} item(s) {string.Join( ", ", items.Select( x => $"{x.Id} '{x.Title}'" ) )}" );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[phrases dbg] {CharadesWorkshop.Sorts[i]} failed: {e.Message}" );
			}
		}

		// No tag filter at all: what does an untagged query of this game's workshop return?
		try
		{
			var all = await new Storage.Query { SortOrder = Storage.SortOrder.RankedByPublicationDate }.Run();
			Log.Info( $"[phrases dbg] untagged newest: {all?.Items?.Count ?? -1} item(s), total {all?.TotalCount ?? -1}: "
				+ string.Join( ", ", (all?.Items ?? new()).Take( 10 ).Select( x => $"{x.Id} '{x.Title}' tags=[{string.Join( "|", x.Tags ?? new() )}]" ) ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[phrases dbg] untagged query failed: {e.Message}" );
		}

		if ( ulong.TryParse( fileId, out var id ) )
		{
			try
			{
				var r = await new Storage.Query { FileIds = new() { id } }.Run();
				foreach ( var x in r?.Items ?? new() )
				{
					var kv = string.Join( "|", (x.KeyValues ?? new()).Select( p => $"{p.Key}={p.Value}" ) );
					Log.Info( $"[phrases dbg] by id {x.Id}: '{x.Title}' visibility={x.Visibility} banned={x.Banned} accepted={x.Accepted} "
						+ $"tags=[{string.Join( "|", x.Tags ?? new() )}] kv=[{kv}] owner={x.Owner?.Name} created={x.Created}" );
				}
				if ( (r?.Items?.Count ?? 0) == 0 )
					Log.Warning( $"[phrases dbg] by id {id}: nothing returned" );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[phrases dbg] by-id query failed: {e.Message}" );
			}
		}
	}
}

public static class CharadesLikesDebug
{
	/// <summary>Debug: dump the global like stats the backend reports for phrase lists.
	/// Usage: <c>mimi_dbg_likes</c>.</summary>
	[ConCmd( "mimi_dbg_likes" )]
	public static async void Run()
	{
		await CharadesWorkshop.RefreshLikes();
		var stats = Sandbox.Services.Stats.Global.Where( s => s.Name.StartsWith( "phrases_like_" ) ).ToList();
		Log.Info( $"[likes dbg] {stats.Count} like stat(s): " + string.Join( ", ", stats.Select( s => $"{s.Name} sum={s.Sum} players={s.Players}" ) ) );
	}
}
