using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.Modals;

namespace Mimiclay;

/// <summary>
/// Community phrase lists on the Steam Workshop, through the engine's <see cref="Storage"/> API (the same
/// route <see cref="SculptWorkshop"/> uses for heads and props). A list is as plain as it gets for the people
/// making them: a title and ONE PHRASE PER LINE — nothing else to learn. On the wire it's a Storage entry of
/// type <see cref="EntryType"/> holding <see cref="FileName"/> (that newline text) plus a couple of meta
/// values the browser can show before downloading (title, phrase count).
///
/// <b>Added lists</b> are the ones this machine has pulled in: installed (silently — only publishing is
/// overlay-mediated, by Steam's design), parsed, and remembered in the local data folder so they're still
/// there next session. They show in the lobby's Phrases panel beside the built-ins (id "ws:&lt;fileId&gt;", see
/// <see cref="CharadesTopics"/>); the host's selection travels to the map as ids, and only the HOST ever reads
/// the phrases (it draws and tells the mimic by targeted RPC), so clients never need a list installed.
/// <see cref="EnsureSelected"/> covers the host arriving in the map with an id it no longer has cached.
///
/// <b>Your own lists</b> (<see cref="Mine"/>) can be edited in place — a re-publish carrying the item's
/// workshop id updates it instead of minting a new one (Storage.Entry.Publish honours the "_workshopId" meta)
/// — and deleted. Games can't remove a workshop item from Steam (no public API), so <see cref="Delete"/>
/// forgets it here, hides it from "Mine", and copies its Steam page link so the author can finish there.
/// </summary>
public static class CharadesWorkshop
{
	/// <summary>Storage entry type, doubling as the workshop tag the browser filters on.</summary>
	public const string EntryType = "phrases";

	/// <summary>The phrases file inside an entry — newline-separated text. FROZEN once shipped (published
	/// items keep the name they were written with).</summary>
	public const string FileName = "phrases.txt";

	/// <summary>Where added lists + deleted ids are remembered between sessions (local data folder).</summary>
	const string SavePath = "charades/phrase_lists.json";

	/// <summary>A community list as the game uses it — the parsed phrases plus what the row shows.</summary>
	public sealed class PhraseList
	{
		public ulong FileId { get; set; }
		public string Title { get; set; } = "Untitled";
		public string Author { get; set; } = "";
		public List<string> Phrases { get; set; } = new();
	}

	sealed class SaveData
	{
		public List<PhraseList> Lists { get; set; } = new();

		/// <summary>Own items the player deleted here — hidden from "Mine" while Steam still lists them.</summary>
		public List<ulong> Deleted { get; set; } = new();

		/// <summary>Lists the player has liked in-game (see <see cref="ToggleLike"/>).</summary>
		public List<ulong> Liked { get; set; } = new();
	}

	static Dictionary<ulong, PhraseList> _added;
	static HashSet<ulong> _deleted;
	static HashSet<ulong> _liked;

	/// <summary>Fires whenever the added set changes (rows need rebuilding).</summary>
	public static event Action Changed;

	static void EnsureLoaded()
	{
		// Both, not just _added: a hotload that adds a static leaves it null while the other survives.
		if ( _added is not null && _deleted is not null && _liked is not null )
			return;

		_added = new Dictionary<ulong, PhraseList>();
		_deleted = new HashSet<ulong>();
		_liked = new HashSet<ulong>();
		try
		{
			var saved = FileSystem.Data.ReadJsonOrDefault<SaveData>( SavePath );
			foreach ( var list in saved?.Lists ?? Enumerable.Empty<PhraseList>() )
			{
				if ( list is { FileId: > 0, Phrases.Count: > 0 } )
					_added[list.FileId] = list;
			}
			foreach ( var id in saved?.Liked ?? Enumerable.Empty<ulong>() )
				_liked.Add( id );
			foreach ( var id in saved?.Deleted ?? Enumerable.Empty<ulong>() )
				_deleted.Add( id );
		}
		catch ( Exception e )
		{
			Log.Warning( $"CharadesWorkshop: couldn't read the saved phrase lists — {e.Message}" );
		}
	}

	static Dictionary<ulong, PhraseList> AddedMap
	{
		get
		{
			EnsureLoaded();
			return _added;
		}
	}

	/// <summary>The community lists this machine has added, in title order.</summary>
	public static IReadOnlyList<PhraseList> Added
		=> AddedMap.Values.OrderBy( l => l.Title, StringComparer.OrdinalIgnoreCase ).ToList();

	public static bool IsAdded( ulong fileId ) => AddedMap.ContainsKey( fileId );

	public static bool TryGet( ulong fileId, out PhraseList list ) => AddedMap.TryGetValue( fileId, out list );

	/// <summary>Drop a list from the added set (its row goes; the workshop item is untouched).</summary>
	public static void Remove( ulong fileId )
	{
		if ( !AddedMap.Remove( fileId ) )
			return;

		Save();
		Changed?.Invoke();
	}

	static void Save()
	{
		EnsureLoaded();
		try
		{
			FileSystem.Data.CreateDirectory( "charades" );
			FileSystem.Data.WriteJson( SavePath, new SaveData { Lists = _added.Values.ToList(), Deleted = _deleted.ToList(), Liked = _liked.ToList() } );
		}
		catch ( Exception e )
		{
			Log.Warning( $"CharadesWorkshop: couldn't save the phrase lists — {e.Message}" );
		}
	}

	/// <summary>A workshop item's Steam page.</summary>
	public static string SteamUrl( ulong fileId ) => $"https://steamcommunity.com/sharedfiles/filedetails/?id={fileId}";

	// ── Browsing ──────────────────────────────────────────────────────────────────────────────────────────
	/// <summary>The browser's sort tabs, in display order. "Mine" is the player's own lists (private ones
	/// included), where Edit and Delete live.</summary>
	public static readonly string[] Sorts = { "Top Rated", "Newest", "Trending", "Mine" };

	/// <summary>Index of the "Mine" tab in <see cref="Sorts"/>.</summary>
	public const int MineSort = 3;

	static readonly Storage.SortOrder[] SortOrders =
	{
		Storage.SortOrder.RankedByVote,
		Storage.SortOrder.RankedByPublicationDate,
		Storage.SortOrder.RankedByTrend,
		Storage.SortOrder.RankedByPublicationDate,
	};

	/// <summary>Query the workshop for phrase lists under the given sort tab. Banned items are dropped, and
	/// "Mine" hides the ones deleted here. Throws on a workshop failure — callers show a status line.</summary>
	public static async Task<List<Storage.QueryItem>> Browse( int sort )
	{
		sort = Math.Clamp( sort, 0, Sorts.Length - 1 );
		// Filter by KEYVALUES, not tags. Storage stamps every published item with type=<entry type> and
		// package=<game ident>; both are searchable at once. Tag search was tried first and returned nothing for a
		// freshly-published list (Steam hadn't indexed the new "phrases" tag yet, while old tags like "prop" worked).
		// Storage queries also span EVERY s&box game's workshop items, so the package filter is what keeps the
		// browser to Mimiclay's lists.
		var query = new Storage.Query { SortOrder = SortOrders[sort] };
		query.KeyValues["type"] = EntryType;
		if ( !string.IsNullOrWhiteSpace( Game.Ident ) )
			query.KeyValues["package"] = Game.Ident;

		if ( sort == MineSort )
			query.Author = Game.SteamId;

		var result = await query.Run();
		var items = result?.Items?.Where( i => i is { Banned: false } ) ?? Enumerable.Empty<Storage.QueryItem>();

		if ( sort == MineSort )
		{
			EnsureLoaded();
			items = items.Where( i => !_deleted.Contains( i.Id ) );
		}

		var list = items.ToList();

		// Top Rated ranks by IN-GAME likes (Steam's own votes break ties). Steam's vote ordering can't see
		// likes, so the fetched page is re-sorted here once the global like counts are in.
		if ( sort == 0 )
		{
			await RefreshLikes();
			list = list.OrderByDescending( Likes ).ThenByDescending( i => i.VotesUp ).ToList();
		}

		return list;
	}

	// ── In-game likes ─────────────────────────────────────────────────────────────────────────────────────
	// Games can't vote on Steam Workshop items (s&box keeps its UGC service internal), so likes live in s&box's
	// own stats backend instead: one stat per list, "phrases_like_<fileId>", that each player SETS to 1 (liked)
	// or 0 (unliked). The stat's global Sum is then the number of players who like it — deduped per player and
	// undoable, unlike an increment. The local liked set (saved with the added lists) drives the heart at once;
	// a per-session delta keeps the count honest until the backend's aggregate catches up.

	const string LikePrefix = "phrases_like_";

	static string LikeStat( ulong fileId ) => LikePrefix + fileId;

	static readonly Dictionary<ulong, int> _likeDelta = new();

	/// <summary>True when the local player has liked this list.</summary>
	public static bool IsLiked( ulong fileId )
	{
		EnsureLoaded();
		return _liked.Contains( fileId );
	}

	/// <summary>How many players like this list: the backend's global count plus this session's own change.</summary>
	public static int Likes( Storage.QueryItem item ) => item is null ? 0 : Likes( item.Id );

	public static int Likes( ulong fileId )
	{
		var global = 0;
		try
		{
			if ( Sandbox.Services.Stats.Global.TryGet( LikeStat( fileId ), out var stat ) )
				global = (int)Math.Round( stat.Sum );
		}
		catch
		{
			// Stats backend unavailable (offline, editor without a published package) — local delta only.
		}

		return Math.Max( 0, global + (_likeDelta.TryGetValue( fileId, out var d ) ? d : 0) );
	}

	/// <summary>Like or unlike a list. Returns the new liked state.</summary>
	public static bool ToggleLike( ulong fileId )
	{
		EnsureLoaded();
		var liked = !_liked.Contains( fileId );
		if ( liked )
			_liked.Add( fileId );
		else
			_liked.Remove( fileId );

		_likeDelta[fileId] = (_likeDelta.TryGetValue( fileId, out var d ) ? d : 0) + (liked ? 1 : -1);
		Save();

		try
		{
			Sandbox.Services.Stats.SetValue( LikeStat( fileId ), liked ? 1 : 0 );
			Sandbox.Services.Stats.Flush();
		}
		catch ( Exception e )
		{
			Log.Warning( $"CharadesWorkshop: couldn't send the like for {fileId} — {e.Message}" );
		}

		Changed?.Invoke();
		return liked;
	}

	/// <summary>Pull fresh global like counts (the stats service throttles this to once every 10s).</summary>
	public static async Task RefreshLikes()
	{
		try
		{
			await Sandbox.Services.Stats.Global.Refresh();
		}
		catch ( Exception e )
		{
			Log.Warning( $"CharadesWorkshop: couldn't refresh like counts — {e.Message}" );
		}
	}

	/// <summary>True when the local player published <paramref name="item"/>.</summary>
	public static bool IsMine( Storage.QueryItem item )
		=> item?.Owner is not null && item.Owner.Id == Game.SteamId;

	/// <summary>The phrase count a published item advertises (its meta), or 0 when it's missing.</summary>
	public static int AdvertisedCount( Storage.QueryItem item )
	{
		if ( item?.Metadata is null )
			return 0;

		try
		{
			// Entry.SetMeta stores each value as JSON text (a string arrives quoted), so read it back the same way.
			var meta = Json.Deserialize<StorageMetaShape>( item.Metadata );
			if ( meta?.Meta is null || !meta.Meta.TryGetValue( "count", out var raw ) )
				return 0;
			return int.TryParse( Json.Deserialize<string>( raw ), out var n ) ? n : 0;
		}
		catch
		{
			return 0;
		}
	}

	// The shape of Storage's _meta.json as it rides QueryItem.Metadata — just enough to read our values.
	sealed class StorageMetaShape
	{
		public Dictionary<string, string> Meta { get; set; }
	}

	// ── Adding ────────────────────────────────────────────────────────────────────────────────────────────
	/// <summary>Install a browsed item (silent download) and add it. Returns the parsed list, or null when the
	/// item has no usable phrases. Already-added items just return the cached list.</summary>
	public static async Task<PhraseList> Add( Storage.QueryItem item )
	{
		if ( item is null )
			return null;

		if ( TryGet( item.Id, out var cached ) )
			return cached;

		var list = await Download( item );
		if ( list is null )
			return null;

		AddedMap[list.FileId] = list;
		Save();
		Changed?.Invoke();
		return list;
	}

	// Install + parse without adding (the editor's "load my list to edit" uses this too).
	static async Task<PhraseList> Download( Storage.QueryItem item )
	{
		var entry = await item.Install();
		return entry is null ? null : Parse( entry, item.Id, item.Title, item.Owner?.Name );
	}

	/// <summary>The phrases of one of the player's own lists, for the editor: the added copy if there is one,
	/// otherwise a fresh download. Null when it can't be fetched.</summary>
	public static async Task<PhraseList> LoadForEdit( Storage.QueryItem item )
	{
		if ( item is null )
			return null;

		return TryGet( item.Id, out var cached ) ? cached : await Download( item );
	}

	/// <summary>Host, arriving in the map: make sure every community id in the selection is installed and
	/// parsed here — the lobby's cache normally has them, but a cleared data folder or a selection saved from
	/// another session shouldn't silently shrink the pool. Missing ids are fetched by file id.</summary>
	public static async Task EnsureSelected( string selection )
	{
		var missing = CharadesTopics.Parse( selection )
			.Select( id => CharadesTopics.TryWorkshopId( id, out var fid ) ? fid : 0UL )
			.Where( fid => fid != 0 && !IsAdded( fid ) )
			.ToList();

		if ( missing.Count == 0 )
			return;

		try
		{
			var result = await new Storage.Query { FileIds = missing }.Run();
			foreach ( var item in result?.Items ?? Enumerable.Empty<Storage.QueryItem>() )
				await Add( item );
		}
		catch ( Exception e )
		{
			Log.Warning( $"CharadesWorkshop: couldn't fetch {missing.Count} selected community list(s) — {e.Message}" );
		}
	}

	static PhraseList Parse( Storage.Entry entry, ulong fileId, string title, string author )
	{
		if ( entry?.Files is null || !entry.Files.FileExists( FileName ) )
			return null;

		var phrases = CharadesWords.ParseLines( entry.Files.ReadAllText( FileName ) );
		if ( phrases.Count == 0 )
			return null;

		return new PhraseList
		{
			FileId = fileId,
			Title = string.IsNullOrWhiteSpace( title ) ? entry.GetMeta( "title", "Untitled" ) : title.Trim(),
			Author = author ?? "",
			Phrases = phrases,
		};
	}

	// ── Publishing ────────────────────────────────────────────────────────────────────────────────────────
	/// <summary>Publish a list from the in-game editor: <paramref name="text"/> is the raw one-per-line box.
	/// <paramref name="existingId"/> non-zero = update that (own) item in place rather than creating a new one.
	/// Opens Steam's publish overlay (user-confirmed, by design — the title/description are presets the player
	/// can edit there). On completion the list is added locally so it's selectable at once. Returns the parsed
	/// phrase count, or 0 when nothing usable was typed (and nothing is published).</summary>
	public static int Publish( string title, string text, ulong existingId = 0, Action<PhraseList> onPublished = null )
	{
		var phrases = CharadesWords.ParseLines( text );
		if ( phrases.Count == 0 )
			return 0;

		var cleanTitle = string.IsNullOrWhiteSpace( title ) ? "My Phrase List" : title.Trim();
		if ( cleanTitle.Length > 64 )
			cleanTitle = cleanTitle[..64];

		var entry = Storage.CreateEntry( EntryType );
		entry.Files.WriteAllText( FileName, string.Join( '\n', phrases ) );
		entry.SetMeta( "title", cleanTitle );
		entry.SetMeta( "count", phrases.Count.ToString() );

		// Editing: the fresh entry claims the existing item, so Publish updates it instead of minting another.
		if ( existingId != 0 )
			entry.SetMeta( "_workshopId", existingId );

		entry.Publish( new WorkshopPublishOptions
		{
			Title = cleanTitle,
			Description = $"{phrases.Count} charades phrases for Mimiclay.",
			Visibility = Storage.Visibility.Public,
			OnComplete = id =>
			{
				if ( id == 0 )
					return;

				EnsureLoaded();
				_deleted.Remove( id );
				var list = new PhraseList
				{
					FileId = id,
					Title = cleanTitle,
					Author = Sandbox.Services.Players.Profile.Local?.Name ?? "",
					Phrases = phrases,
				};
				AddedMap[id] = list;
				Save();
				Changed?.Invoke();
				onPublished?.Invoke( list );
			},
		} );

		return phrases.Count;
	}

	/// <summary>Delete one of the player's own lists as far as a game can: forget it here (added copy and any
	/// local Storage entries for it), hide it from "Mine", and copy its Steam page link to the clipboard — the
	/// Workshop item itself can only be removed on Steam, where that link leads.</summary>
	public static void Delete( ulong fileId )
	{
		if ( fileId == 0 )
			return;

		EnsureLoaded();
		_added.Remove( fileId );
		_deleted.Add( fileId );

		foreach ( var entry in Storage.GetAll( EntryType ) )
		{
			if ( entry.GetMeta<ulong>( "_workshopId" ) == fileId )
				entry.Delete();
		}

		Save();
		Changed?.Invoke();

		try
		{
			Sandbox.UI.Clipboard.SetText( SteamUrl( fileId ) );
		}
		catch ( Exception e )
		{
			Log.Warning( $"CharadesWorkshop: couldn't copy the Steam link — {e.Message}" );
		}
	}
}

/// <summary>The two views of the lobby's workshop phrase-list window (<c>WorkshopListsWindow</c>).</summary>
public enum WorkshopListsView
{
	Browse,
	Make,
}
