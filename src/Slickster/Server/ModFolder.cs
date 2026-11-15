using SPTarkov.Server.Core.Helpers.Server;

namespace Slickster.Server;

/// <summary>Where this mod's own files live: its mod folder, or its "Slickster" folder when it is built into WWC (mods/WWC).</summary>
internal static class ModFolder
{
    public static string Of(ModHelper helper) =>
#if WWC
        WWC.Server.Parts.Dir(WWC.Server.Parts.Slickster);
#else
        helper.GetAbsolutePathToModFolder(System.Reflection.Assembly.GetExecutingAssembly());
#endif
}