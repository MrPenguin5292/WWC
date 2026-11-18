using SPTarkov.Server.Core.Helpers.Server;

namespace JackalArmbands.Server;

/// <summary>Where this mod's own files live: its mod folder, or its "Armbands" folder inside WWC when it is built into WWC's Forge build (mods/WWC).</summary>
internal static class PartFolder
{
    public static string Of(ModHelper helper) =>
#if WWC
        WWC.Server.Parts.Dir(WWC.Server.Parts.Armbands);
#else
        helper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
#endif
}
