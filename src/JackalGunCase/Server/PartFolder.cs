using SPTarkov.Server.Core.Helpers.Server;

namespace JackalGunCase.Server;

/// <summary>Where this mod's own files live: its mod folder, or its "GunCase" folder inside WWC when it is built into WWC's Forge build (mods/WWC).</summary>
internal static class PartFolder
{
    public static string Of(ModHelper helper) =>
#if WWC
        WWC.Server.Parts.Dir(WWC.Server.Parts.GunCase);
#else
        helper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
#endif
}
