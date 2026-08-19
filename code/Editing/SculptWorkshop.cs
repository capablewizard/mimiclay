using System;
using System.Threading.Tasks;
using Sandbox.Modals;
using Sandbox.UI;

namespace Mimiclay;

/// <summary>
/// The brain behind the EditHud's Workshop column — Save To Workshop, Load From Workshop and the library
/// window. Extracted from MenuCustomise so creative mode can mount the same buttons on the in-game HUD:
/// an OWNER (MenuCustomise for the menu customiser, CreativeManager in a creative map) creates an instance
/// configured for what's being sculpted (<see cref="ForHeads"/> / <see cref="ForProps"/>), points the HUD's
/// Workshop* actions at it, and clears those actions when its context ends. The instance never wires the
/// HUD itself — ownership of the HUD's state stays in one place per scene.
///
/// Saving publishes a FRESH Storage entry every time (a new workshop item): the workshop is a library, and
/// update-in-place meant a save after a load silently overwrote the original item — overwriting must never
/// be implicit. Loading is entirely silent API (only publishing is overlay-mediated), so the browser is our
/// own UI: the query is scoped to this app's workshop, filtered to <see cref="EntryType"/>, and restricted
/// to Author = the local player (which is also what lets it see Private items).
///
/// Every async hop re-checks <see cref="_alive"/> (plus the live target/session), so a torn-down context —
/// customise page left, creative pawn swapped away mid-download — just abandons the operation.
/// </summary>
public sealed class SculptWorkshop
{
	readonly Func<EditHud> _hud;
	readonly Func<SculptEditSession> _session;
	readonly Func<bool> _alive;

	SculptWorkshop( Func<EditHud> hud, Func<SculptEditSession> session, Func<bool> alive )
	{
		_hud = hud;
		_session = session;
		_alive = alive;
	}

	/// <summary>Storage entry type, doubling as the workshop tag the browser filters on. Heads and props are
	/// separate libraries: a creative prop must never show up in the customiser's head browser (or land on a
	/// face when picked there).</summary>
	public string EntryType { get; init; } = "head";

	/// <summary>The sculpt JSON's file name inside the entry. Per-type and FROZEN once shipped — published
	/// items keep the name they were written with, so the reader must ask for exactly what its type saves.</summary>
	public string FileName { get; init; } = "head.sculpt";

	/// <summary>Fallback display name: the JSON entry's Name and an untitled item's tile label.</summary>
	public string EntryName { get; init; } = "Head";

	/// <summary>Preset title/description on the publish overlay (the player can edit both there).</summary>
	public string PublishTitle { get; init; } = "My Mimiclay Head";

	/// <inheritdoc cref="PublishTitle"/>
	public string PublishDescription { get; init; } = "A head sculpted in Mimiclay.";

	/// <summary>Browser status line when the player's library query comes back empty.</summary>
	public string EmptyStatus { get; init; } = "No saved heads found";

	/// <summary>Browser status line when a picked item can't be installed/parsed/loaded.</summary>
	public string LoadFailStatus { get; init; } = "Couldn't load that head";

	/// <summary>The menu customiser's flavor: the player's published heads.</summary>
	public static SculptWorkshop ForHeads( Func<EditHud> hud, Func<SculptEditSession> session, Func<bool> alive )
		=> new( hud, session, alive );

	/// <summary>Creative mode's flavor: sculpted props, published under their own tag.</summary>
	public static SculptWorkshop ForProps( Func<EditHud> hud, Func<SculptEditSession> session, Func<bool> alive )
		=> new( hud, session, alive )
		{
			EntryType = "prop",
			FileName = "prop.sculpt",
			EntryName = "Prop",
			PublishTitle = "My Mimiclay Prop",
			PublishDescription = "A prop sculpted in Mimiclay.",
			EmptyStatus = "No saved props found",
			LoadFailStatus = "Couldn't load that prop",
		};

	// Save To Workshop: pack the current sculpt into a Storage entry (the same JSON a local .sculpt save
	// carries) and hand it to the Steam Workshop publish overlay — user-confirmed every time, by Steam's
	// design (the silent UgcPublisher is engine-internal on purpose).

	bool _thumbCapturing;

	/// <summary>The Save button. Async: renders the thumbnail first (it spans a few frames, and the publish
	/// modal should open showing it), then publishes a fresh entry.</summary>
	public async void Save()
	{
		var target = _session()?.Target;
		if ( _thumbCapturing || !_alive() || !target.IsValid() || target.Brushes is not { Count: > 0 } )
			return;

		_thumbCapturing = true;
		Bitmap thumb;
		try
		{
			thumb = await CaptureBitmap();
		}
		finally
		{
			_thumbCapturing = false;
		}

		// The capture awaited across frames — bail if the context ended (page left, pawn swapped) meanwhile.
		// The target is re-resolved rather than reused: a session can outlive one target and pick up another.
		target = _session()?.Target;
		if ( !_alive() || !target.IsValid() || target.Brushes is not { Count: > 0 } )
			return;

		var entry = Storage.CreateEntry( EntryType );
		entry.Files.WriteAllText( FileName, Json.Serialize( new SculptLibrary.Entry
		{
			Name = EntryName,
			Resolution = target.Resolution,
			FlipFaces = target.FlipFaces,
			Brushes = target.Brushes,
		} ) );

		// Stored ON the entry (not just passed to the modal): Publish reads the entry's _thumb.png into the
		// options itself, and the saved file doubles as the local gallery icon for the future Library page.
		if ( thumb is not null )
			entry.SetThumbnail( thumb );

		entry.Publish( new WorkshopPublishOptions
		{
			Title = PublishTitle,
			Description = PublishDescription,
			Visibility = Storage.Visibility.Private, // preset only — the modal's visibility selector is left on
		} );
	}

	// Render the current sculpt through the roster-icon pipeline (SdfThumbnail → SdfStage: same rig prefab,
	// same ink outline, prop on transparency) and read the pixels back as a workshop-ready Bitmap. A
	// ScenePanel is the only sanctioned runtime render-to-texture route (see SdfThumbnail's header), so the
	// capture rig IS a panel — parked invisible on the EditHud, ticked by the UI for a few frames while it
	// stages and renders, then read back and deleted.
	async Task<Bitmap> CaptureBitmap()
	{
		var hud = _hud();
		var host = hud.IsValid() ? hud.Panel : null;
		var target = _session()?.Target;
		if ( host is null || !target.IsValid() )
			return null;

		var thumb = new SdfThumbnail
		{
			Parent = host,
			Brushes = target.Brushes.Where( b => !b.Damage ).Select( b => b.Copy() ).ToList(),
		};

		// Invisible but laid out: the panel needs a real rect to size its render target. Opacity only hides
		// the on-screen draw — the offscreen render still happens. (Panels don't take pointer events unless
		// styled to, so this can't block the HUD while it exists.)
		thumb.Style.Position = PositionMode.Absolute;
		thumb.Style.Left = 0;
		thumb.Style.Top = 0;
		thumb.Style.Width = 512;
		thumb.Style.Height = 512;
		thumb.Style.Opacity = 0;

		try
		{
			// A few UI ticks: layout a rect, stage the brushes, render. HasSubject + a live RenderTexture is
			// the "picture landed" signal; the deadline covers a stage that can't come up without hanging.
			// The frame awaits ride the capture panel's own TaskSource (this class isn't a component, so it
			// has none of its own), which also cancels the capture if the panel is torn down under it.
			for ( int i = 0; i < 30; i++ )
			{
				await thumb.Task.Frame();
				if ( thumb.HasSubject && thumb.RenderTexture is not null )
					break;
			}

			await thumb.Task.Frame(); // one more so the render queued by the final stage/frame change has landed

			var tex = thumb.RenderTexture;
			if ( tex is null )
				return null;

			// Keep the stage's transparency: the sculpt floats on alpha with its ink outline, so the library
			// tiles show no backdrop square. (Steam's docs suggest opaque previews but alpha PNGs are
			// accepted fine — the workshop site just draws its own ground behind them.) Then square it to
			// the 512×512 the workshop asks for.
			var src = tex.GetPixels();
			var pixels = new Color[src.Length];

			for ( int i = 0; i < src.Length; i++ )
				pixels[i] = src[i].ToColor();

			var bitmap = new Bitmap( tex.Width, tex.Height );
			bitmap.SetPixels( pixels );

			return tex.Width == 512 && tex.Height == 512 ? bitmap : bitmap.Resize( 512, 512 );
		}
		finally
		{
			// NOT Delete(): this finally runs in an await continuation, which the engine can resume from
			// inside the capture panel's own internal-scene tick — a synchronous delete there destroys that
			// scene mid-tick and the resumed tick NREs in Nav_Update. DeleteSoon defers to the panel's own
			// next tick, where its scene is guaranteed idle.
			thumb.DeleteSoon();
		}
	}

	bool _busy;

	/// <summary>The Load button: open the library window and fill it with the player's published items of
	/// this type. A second click toggles the window closed (the window's own X routes to <see cref="Close"/>).</summary>
	public async void Load()
	{
		var hud = _hud();
		if ( !hud.IsValid() )
			return;

		if ( hud.WorkshopBrowserOpen )
		{
			Close();
			return;
		}

		if ( _busy )
			return;

		_busy = true;
		hud.WorkshopBrowserOpen = true;
		hud.WorkshopItems = null;
		hud.WorkshopStatus = "Searching…";

		try
		{
			var query = new Storage.Query
			{
				Author = Game.SteamId,
				TagsRequired = { EntryType },
				SortOrder = Storage.SortOrder.RankedByPublicationDate,
			};

			var result = await query.Run();

			hud = _hud();
			if ( !_alive() || !hud.IsValid() || !hud.WorkshopBrowserOpen )
				return; // context ended or window closed while searching

			var items = result?.Items?.Where( i => !i.Banned ).ToList();
			if ( items is not { Count: > 0 } )
			{
				hud.WorkshopStatus = EmptyStatus;
				return;
			}

			hud.WorkshopStatus = null;
			hud.WorkshopItems = items
				.Select( i => (
					string.IsNullOrWhiteSpace( i.Title ) ? EntryName : i.Title,
					i.Preview,
					(Action)(() => Apply( i )) ) )
				.ToList();
		}
		catch ( Exception e )
		{
			Log.Warning( $"SculptWorkshop: workshop query failed — {e.Message}" );
			hud = _hud();
			if ( hud.IsValid() )
				hud.WorkshopStatus = "Workshop unavailable";
		}
		finally
		{
			_busy = false;
		}
	}

	/// <summary>Close the library window (the window's X, and what a successful load does itself).</summary>
	public void Close()
	{
		var hud = _hud();
		if ( !hud.IsValid() )
			return;

		hud.WorkshopBrowserOpen = false;
		hud.WorkshopItems = null;
		hud.WorkshopStatus = null;
	}

	// Download the picked item (silent) and apply it — through the session's Load funnel, so it's undoable,
	// rebuilds/commits, and any persist hook (the head slot) writes it through as a normal edit would.
	async void Apply( Storage.QueryItem item )
	{
		var hud = _hud();
		if ( _busy || !hud.IsValid() )
			return;

		_busy = true;
		hud.WorkshopStatus = "Downloading…"; // tiles stay up; the busy flag blanks double-clicks

		try
		{
			var installed = await item.Install();

			hud = _hud();
			if ( !_alive() || !hud.IsValid() )
				return;

			var json = installed?.Files.FileExists( FileName ) == true
				? installed.Files.ReadAllText( FileName )
				: null;
			var entry = json is null ? null : Json.Deserialize<SculptLibrary.Entry>( json );

			var session = _session();
			if ( entry is null || !session.IsValid() || !session.Load( entry ) )
			{
				hud.WorkshopStatus = LoadFailStatus;
				return;
			}

			// Success — the sculpt is on; close the window so the player sees it. (Deliberately NOT
			// remembering the item's id for the next save: saves always mint a new item — sculpting over a
			// loaded shape and saving must never silently overwrite the original.)
			Close();
		}
		catch ( Exception e )
		{
			Log.Warning( $"SculptWorkshop: workshop install failed — {e.Message}" );
			hud = _hud();
			if ( hud.IsValid() )
				hud.WorkshopStatus = LoadFailStatus;
		}
		finally
		{
			_busy = false;
		}
	}
}
