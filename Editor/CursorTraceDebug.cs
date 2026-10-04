using System;
using System.Linq;
using System.Reflection;
using Sandbox;

namespace Editor;

/// <summary>
/// TEMPORARY diagnostic: logs every change to the cursor the engine actually applies (InputRouter's private
/// CursorName + MouseCursorVisible) alongside the game's Mouse.CursorType, so a "system cursor after click" glitch
/// can be traced to whoever wrote it. Toggle with the mimi_dbg_cursor_trace convar. Delete once diagnosed.
/// </summary>
public static class CursorTraceDebug
{
	[ConVar( "mimi_dbg_cursor_trace" )]
	public static bool Enabled { get; set; }

	static readonly Type RouterType = typeof( Mouse ).Assembly.GetType( "Sandbox.Engine.InputRouter" );
	static readonly PropertyInfo CursorNameProp = RouterType?.GetProperty( "CursorName", BindingFlags.Static | BindingFlags.NonPublic );
	static readonly PropertyInfo VisibleProp = RouterType?.GetProperty( "MouseCursorVisible", BindingFlags.Static | BindingFlags.Public );
	static readonly FieldInfo CaptureField = RouterType?.GetField( "mouseCapturePosition", BindingFlags.Static | BindingFlags.NonPublic );
	static readonly Type WindowInputType = typeof( Mouse ).Assembly.GetType( "Sandbox.Engine.WindowInput" );
	static readonly FieldInfo FocusedField = WindowInputType?.GetField( "focused", BindingFlags.Static | BindingFlags.NonPublic );
	static readonly FieldInfo ActiveField = WindowInputType?.GetField( "active", BindingFlags.Static | BindingFlags.NonPublic );
	static readonly Type SdlCursorsType = typeof( Mouse ).Assembly.GetType( "Sandbox.Engine.SdlCursors" );
	static readonly FieldInfo TempCursorField = SdlCursorsType?.GetField( "temporaryCursor", BindingFlags.Static | BindingFlags.NonPublic );

	static string _last;
	static bool _reportedV4;

	[EditorEvent.Frame]
	static void Frame()
	{
		if ( !Enabled )
			return;

		if ( !_reportedV4 )
		{
			_reportedV4 = true;
			Log.Info( $"[CursorTrace] engine asm: {typeof( Mouse ).Assembly.Location}" );
			if ( RouterType is not null )
				Log.Info( $"[CursorTrace] all static nonpublic: {string.Join( ", ", RouterType.GetMembers( BindingFlags.Static | BindingFlags.NonPublic ).Select( m => m.Name ) )}" );
			if ( RouterType is not null )
			{
				var names = RouterType.GetMembers( BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public ).Select( m => $"{m.MemberType}:{m.Name}" ).Where( n => n.Contains( "ursor", StringComparison.OrdinalIgnoreCase ) );
				Log.Info( $"[CursorTrace] members: {string.Join( ", ", names )}" );
			}
			Log.Info( $"[CursorTrace] router={RouterType?.FullName ?? "NULL"} cursorNameProp={(CursorNameProp is null ? "NULL" : "ok")} visibleProp={(VisibleProp is null ? "NULL" : "ok")} captureField={(CaptureField is null ? "NULL" : "ok")}" );
		}

		string applied = null, visible = null, game = null, lmb = null, capture = null, focus = null;
		try
		{
			applied = CursorNameProp?.GetValue( null ) as string;
			visible = VisibleProp?.GetValue( null )?.ToString();
			game = Mouse.CursorType;
			lmb = Sandbox.Input.Down( "attack1" ).ToString();
			capture = CaptureField?.GetValue( null ) is null ? "no" : "YES";
			focus = $"active={ActiveField?.GetValue( null )} focused={FocusedField?.GetValue( null )} tempArrow={TempCursorField?.GetValue( null )}";
		}
		catch ( Exception e )
		{
			applied = $"<{e.GetType().Name}>";
		}

		var line = $"applied='{applied}' visible={visible} Mouse.CursorType='{game}' lmb={lmb} captured={capture} {focus}";
		if ( line == _last )
			return;

		_last = line;
		Log.Info( $"[CursorTrace] {line}" );
	}
}
