using SPTarkov.Server.Core.Helpers.Server;

namespace WWCHelmetDisplays.Server;

/// <summary>Where this mod's own files live: its mod folder, or its "HelmetDisplays" folder inside WWC when it is built into WWC's Forge build (mods/WWC).</summary>
internal static class PartFolder
{
    public static string Of(ModHelper helper) =>
#if WWC
        WWC.Server.Parts.Dir(WWC.Server.Parts.HelmetDisplays);
#else
        helper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
#endif
}
