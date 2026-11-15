using SPTarkov.Server.Core.Helpers.Server;

namespace JackalOrigin.Server;

/// <summary>Where this mod's own files live: its mod folder, or its "Origin" folder when it is built into WWC (mods/WWC).</summary>
internal static class ModFolder
{
    public static string Of(ModHelper helper) =>
#if WWC
        WWC.Server.Parts.Dir(WWC.Server.Parts.Origin);
#else
        helper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
#endif
}