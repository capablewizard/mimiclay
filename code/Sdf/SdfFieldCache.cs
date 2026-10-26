using System;
using System.Collections.Generic;
using System.Linq;

namespace Mimiclay;

/// <summary>
/// Shared, refcounted store of baked GPU distance fields (<see cref="SdfFieldGpu"/>), keyed by the renderer's
/// field hash (brush content + resolution + sparse flag + baked-displacement params + boil tick).
///
/// Before this every <see cref="SdfRaymarchRenderer"/> owned a private field: a crowd of 150 props from 42
/// prefabs held 150 fields (~4.2 MB each at the shipped 195–256 res, 667 MB) and dispatched 150 bakes at load.
/// Identical shapes share one field; unique props still own independent fields and bake inputs.
///
/// Edit paths keep their old behaviour by construction: a renderer whose hash changes while it is the SOLE
/// owner of its field re-evaluates that same field object in place (same textures, no allocation — exactly
/// what a live drag or a boil tick did before) and the entry is re-keyed. Only a renderer that shares its
/// field detaches and bakes a fresh one, and only when the new hash isn't already cached. Released entries
/// (refcount 0) linger for <see cref="UnusedGraceSeconds"/> so a prop that re-enables, or a shape that an
/// undo brings straight back, costs nothing; then their textures are disposed deterministically — previously
/// nothing ever disposed a field, scene changes left old and new sets coexisting until the GC got round to it.
/// </summary>
public static class SdfFieldCache
{
	sealed class Entry
	{
		public SdfFieldGpu Field;
		public int Key;
		public int Refs;
		public RealTimeSince Unused;
	}

	static readonly Dictionary<int, Entry> _byKey = new();
	static readonly Dictionary<SdfFieldGpu, Entry> _byField = new();

	/// <summary>How long a field with no owners stays resident before its textures are disposed.</summary>
	public const float UnusedGraceSeconds = 3f;

	/// <summary>Resident byte budget for UNOWNED (grace) entries; beyond it the oldest are evicted at once.</summary>
	public const long UnownedBudgetBytes = 256L * 1024 * 1024;

	static RealTimeSince _sinceSweep;

	/// <summary>Number of cached fields (owned + in grace).</summary>
	public static int Count => _byKey.Count;

	/// <summary>Cached fields with at least one owner.</summary>
	public static int OwnedCount => _byKey.Values.Count( e => e.Refs > 0 );

	/// <summary>Sum of owner references — i.e. how many renderers would have held a private field without sharing.</summary>
	public static int TotalRefs => _byKey.Values.Sum( e => e.Refs );

	/// <summary>GPU payload across every cached field (owned + grace), including bake inputs and text.</summary>
	public static long ResidentBytes => _byKey.Values.Sum( e => e.Field.EstimatedBytes );
	public static long FieldTextureBytes => _byKey.Values.Sum( e => e.Field.FieldTextureBytes );
	public static long InputTextureBytes => _byKey.Values.Sum( e => e.Field.InputTextureBytes );
	public static long TextAtlasBytes => _byKey.Values.Sum( e => e.Field.TextAtlasBytes );

	/// <summary>Bytes held by unowned (grace) entries only.</summary>
	public static long UnownedBytes => _byKey.Values.Where( e => e.Refs == 0 ).Sum( e => e.Field.EstimatedBytes );

	/// <summary>Get the field for <paramref name="key"/>, taking a reference on it. <paramref name="current"/> is the
	/// field the caller holds now (null if none); its reference is released or re-keyed as appropriate.
	/// <paramref name="bake"/> tells the caller whether the returned field still needs <see cref="SdfFieldGpu.Evaluate"/>
	/// for this key (a cache hit is already baked). The caller MUST evaluate when asked, or the entry is a lie.</summary>
	public static SdfFieldGpu Acquire( int key, SdfFieldGpu current, out bool bake )
	{
		Sweep();

		// Hit: share the existing bake.
		if ( _byKey.TryGetValue( key, out var hit ) )
		{
			if ( hit.Field != current )
			{
				Release( current );
				hit.Refs++;
			}
			bake = false;
			return hit.Field;
		}

		// Sole owner of the current field: re-evaluate it in place (textures reused, no allocation) and re-key.
		if ( current is not null && _byField.TryGetValue( current, out var mine ) && mine.Refs == 1 )
		{
			_byKey.Remove( mine.Key );
			mine.Key = key;
			_byKey[key] = mine;
			bake = true;
			return current;
		}

		// Shared (or no) field: detach and start a fresh one for this key.
		Release( current );
		var entry = new Entry { Field = new SdfFieldGpu(), Key = key, Refs = 1 };
		_byKey[key] = entry;
		_byField[entry.Field] = entry;
		bake = true;
		return entry.Field;
	}

	/// <summary>Drop one reference. The field stays resident through the grace period, then its textures go.</summary>
	public static void Release( SdfFieldGpu field )
	{
		if ( field is null || !_byField.TryGetValue( field, out var entry ) )
			return;

		entry.Refs = Math.Max( 0, entry.Refs - 1 );
		if ( entry.Refs == 0 )
			entry.Unused = 0f;
	}

	/// <summary>A bake into an acquired field failed (no compute shader, texture creation refused): forget the entry
	/// so no other renderer can "hit" an invalid field, and release the caller's reference.</summary>
	public static void Discard( SdfFieldGpu field )
	{
		if ( field is null || !_byField.TryGetValue( field, out var entry ) )
			return;
		_byKey.Remove( entry.Key );
		_byField.Remove( field );
		field.Dispose();
	}

	/// <summary>Evict grace entries past their time, and the oldest beyond the unowned budget. Cheap; runs at most
	/// a few times a second, including frames in which no new fields are acquired.</summary>
	public static void Sweep()
	{
		if ( _sinceSweep < 0.25f )
			return;
		_sinceSweep = 0f;

		List<Entry> dead = null;
		long unowned = 0;
		foreach ( var e in _byKey.Values )
		{
			if ( e.Refs > 0 ) continue;
			if ( e.Unused > UnusedGraceSeconds ) { (dead ??= new()).Add( e ); continue; }
			unowned += e.Field.EstimatedBytes;
		}

		if ( unowned > UnownedBudgetBytes )
		{
			foreach ( var e in _byKey.Values.Where( e => e.Refs == 0 && (dead is null || !dead.Contains( e )) ).OrderByDescending( e => (float)e.Unused ) )
			{
				if ( unowned <= UnownedBudgetBytes ) break;
				(dead ??= new()).Add( e );
				unowned -= e.Field.EstimatedBytes;
			}
		}

		if ( dead is null )
			return;

		foreach ( var e in dead )
		{
			_byKey.Remove( e.Key );
			_byField.Remove( e.Field );
			e.Field.Dispose();
		}
	}

	/// <summary>Dispose everything (scene teardown / debug).</summary>
	public static void Clear()
	{
		foreach ( var e in _byKey.Values )
			e.Field.Dispose();
		_byKey.Clear();
		_byField.Clear();
	}

	public static string DebugLine()
		=> $"fieldCache(global) entries={Count} owned={OwnedCount} refs={TotalRefs} payload={ResidentBytes / 1048576.0:0.0}MiB fields={FieldTextureBytes / 1048576.0:0.0}MiB inputs={InputTextureBytes / 1048576.0:0.0}MiB text={TextAtlasBytes / 1048576.0:0.0}MiB grace={UnownedBytes / 1048576.0:0.0}MiB";
}

/// <summary>Expire unowned fields even after the last renderer stops requesting bakes.
/// The cache is shared across scenes; Sweep only evicts entries with no owners.</summary>
public sealed class SdfFieldCacheSystem : GameObjectSystem
{
	public SdfFieldCacheSystem( Scene scene ) : base( scene )
	{
		Listen( Stage.FinishUpdate, 100, SdfFieldCache.Sweep, "SdfFieldCache" );
	}
}
