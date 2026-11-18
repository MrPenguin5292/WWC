using SPTarkov.Server.Core.Helpers.Server;

namespace USMCPC.Server;

/// <summary>Where this mod's own files live: its mod folder, or its "USMCPC" folder inside WWC when it is built into WWC's Forge build (mods/WWC).</summary>
internal static class PartFolder
{
    public static string Of(ModHelper helper) =>
#if WWC
        WWC.Server.Parts.Dir(WWC.Server.Parts.USMCPC);
#else
        helper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
#endif
}
