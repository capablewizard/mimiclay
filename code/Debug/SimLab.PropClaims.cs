namespace Mimiclay;

public sealed partial class PropClaims
{
	/// <summary>SimLab (host): make a scene clay prop a LOOSE released prop now, exactly as a creative shot does
	/// (OnCreativePropShot): converted to host-owned released scenery, then marked loose (a free rigidbody in the sim).
	/// Returns the new prop's GameObject, or null if it can't be claimed. See code/Debug/SimLab.cs.</summary>
	internal GameObject LabMakeLoose( SdfSculpture sculpt )
	{
		if ( (Networking.IsActive && !Networking.IsHost) || !sculpt.IsValid() || !IsClaimable( sculpt ) || !_claimedScene.Add( sculpt.GameObject.Id ) )
			return null;
		var pawn = ConvertSceneProp( sculpt, null, scenery: true );
		if ( !pawn.IsValid() )
		{
			_claimedScene.Remove( sculpt.GameObject.Id );
			return null;
		}
		pawn.Components.Get<HiderController>()?.ReleaseControl();
		ReleasedProps[pawn.Id] = true;
		LooseProps[pawn.Id] = true;
		return pawn;
	}
}
