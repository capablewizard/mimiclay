using System.Diagnostics;
using System.IO;
using System.Linq;
using Sandbox;

namespace Editor;

/// <summary>
/// Mimiclay ▸ SimLab: run the networked-sim collision lab from the editor (Tools/SimLab/README.md). The test itself is
/// the Git Bash harness <c>Tools/SimLab/lab.sh</c>, started in its own console window (it drives this editor and its
/// three test clients through sbox-cli, so nothing here blocks the editor).
/// </summary>
public static class SimLabMenu
{
	static string Root => Project.Current.GetRootPath();

	[Menu( "Editor", "Mimiclay/SimLab/Run Full Test", "science" )]
	public static void RunFull()
		=> Dialog.AskConfirm( () => Launch( "full --pause" ),
			"Runs every SimLab scenario once - about 6 minutes.\n\n"
			+ "It stops play, opens scenes/tests/simlab.scene and plays it as the host, launches three test clients, "
			+ "and saves a summary to Tools/SimLab/out/summary_latest.txt. It runs in its own console window: close that "
			+ "window to abort, then use Close Test Clients.",
			"SimLab full test", "Run", "Cancel" );

	[Menu( "Editor", "Mimiclay/SimLab/Run Continuously", "repeat" )]
	public static void RunLoop()
		=> Dialog.AskConfirm( () => Launch( "loop --pause" ),
			"Runs every SimLab scenario over and over until you stop it - handy for watching from your own client "
			+ "(press B for the free cam; the stations are laid out in Tools/SimLab/README.md).\n\n"
			+ "It stops play, opens scenes/tests/simlab.scene and plays it as the host, and launches three test clients. "
			+ "Stop it with Mimiclay > SimLab > Stop Continuous Run: it finishes the current scenario, closes the clients "
			+ "and leaves the running summary in Tools/SimLab/out/summary_latest.txt.",
			"SimLab continuous run", "Run", "Cancel" );

	[Menu( "Editor", "Mimiclay/SimLab/Stop Continuous Run", "stop" )]
	public static void StopLoop()
	{
		var dir = Path.Combine( Root, "Tools", "SimLab", "out" );
		Directory.CreateDirectory( dir );
		File.WriteAllText( Path.Combine( dir, "STOP" ), "stop" ); // (lab.sh loop checks for it between scenarios)
		Log.Info( "SimLab: stopping the continuous run after the current scenario." );
	}

	[Menu( "Editor", "Mimiclay/SimLab/Launch Observer", "visibility" )]
	public static void LaunchObserver() => Launch( "watch" ); // (a 4th client whose free cam flies to each scenario as it runs)

	[Menu( "Editor", "Mimiclay/SimLab/Open Results Folder", "folder_open" )]
	public static void OpenResults()
	{
		var dir = Path.Combine( Root, "Tools", "SimLab", "out" );
		Directory.CreateDirectory( dir );
		Process.Start( new ProcessStartInfo( "explorer.exe", $"\"{dir}\"" ) { UseShellExecute = true } );
	}

	[Menu( "Editor", "Mimiclay/SimLab/Close Test Clients", "close" )]
	public static void CloseClients() => Launch( "close" );

	static void Launch( string args )
	{
		var bash = new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe" }.FirstOrDefault( File.Exists );
		if ( bash is null )
		{
			Log.Warning( "SimLab: Git Bash not found (C:\\Program Files\\Git\\bin\\bash.exe) - run Tools/SimLab/lab.sh " + args + " from a Git Bash." );
			return;
		}
		var root = Root.Replace( '\\', '/' );
		// cmd's START opens bash in a console window of its own; the editor doesn't wait for it.
		Process.Start( new ProcessStartInfo( "cmd.exe", $"/c start \"SimLab\" \"{bash}\" -lc \"cd '{root}' && Tools/SimLab/lab.sh {args}\"" )
		{
			UseShellExecute = false,
			CreateNoWindow = true,
		} );
		Log.Info( $"SimLab: started 'Tools/SimLab/lab.sh {args}' in its own window." );
	}
}
