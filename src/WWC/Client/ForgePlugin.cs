#if WWC_FORGE
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

// The RC bomb car's BigBrain bot layer lives in its own small assembly (WWC.BigBrain.dll next to this one), loaded only when BigBrain is
// installed: a class that derives from a BigBrain type cannot load without it, and inside WWC.Client it made every assembly-wide type scan
// (HarmonyX's AccessTools.TypeByName, other mods' GetTypes) hit a ReflectionTypeLoadException on players without BigBrain.
[assembly: InternalsVisibleTo("WWC.BigBrain")]

namespace WWC.Client;

/// <summary>
/// The one BepInEx plugin of the Forge build (the Forge wants a single plugin whose GUID, name and version match the server mod).
/// It adds the client parts as components, in the order BepInEx loaded them as separate plugins (by GUID). Their Harmony priorities
/// (the patches' item hook runs last) do not depend on this order. The lines folded in for 2.1 read their files from their own folder
/// under BepInEx\plugins\WWC (PartAt).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
[BepInDependency("xyz.drakia.bigbrain", BepInDependency.DependencyFlags.SoftDependency)]   // the RC bomb car's bot layer, when BigBrain is installed
public sealed class ForgePlugin : BaseUnityPlugin
{
    public const string Guid = "com.mrpenguin5292.wwc";
    public const string Name = "MrPenguin5292-WWC";
    public const string Version = "2.1.1";

    /// <summary>The DLL's location, which the original parts use to find their files next to it.</summary>
    internal static PartInfo Part { get; private set; } = new(string.Empty);

    /// <summary>WWC's settings file (BepInEx\config\com.mrpenguin5292.wwc.cfg), shared by the parts that have settings.</summary>
    internal static ConfigFile Cfg { get; private set; } = null!;

    /// <summary>A folded-in line's files: BepInEx\plugins\WWC\&lt;folder&gt;, given as the location of a DLL inside that folder.</summary>
    internal static PartInfo PartAt(string folder) => new(Path.Combine(Path.GetDirectoryName(Part.Location) ?? string.Empty, folder, "WWC.Client.dll"));

    private void Awake()
    {
        Part = new PartInfo(Info.Location);
        Cfg = Config;
        var go = gameObject;
        go.AddComponent<Airlite.Client.Plugin>();
        go.AddComponent<AvonMask.Client.Plugin>();
        go.AddComponent<FTHS.Client.Plugin>();
        go.AddComponent<Grunts.Client.Plugin>();
        go.AddComponent<JackalArmbands.Client.Plugin>();
        go.AddComponent<JackalGunCase.Client.Plugin>();
        go.AddComponent<JackalPatchCase.Client.Plugin>();
        go.AddComponent<JackalPatches.Client.Plugin>();
        go.AddComponent<JPC.Client.Plugin>();
        go.AddComponent<RCBombCar.Client.Plugin>();
        go.AddComponent<Slickster.Client.Plugin>();
        go.AddComponent<USMCPC.Client.Plugin>();
        go.AddComponent<Vape.Client.Plugin>();
        go.AddComponent<WallFlag.Client.Plugin>();
        go.AddComponent<WWCGunDisplays.Client.Plugin>();
        go.AddComponent<WWCHelmetDisplays.Client.Plugin>();
        go.AddComponent<WWCMags.Client.Plugin>();
        go.AddComponent<WWCWraps.Client.Plugin>();
        go.AddComponent<ZynTin.Client.Plugin>();
        // every part's config file in WWC's own F12 entry, next to the parts' game-side settings
        WWC.Client.Settings.SettingsCore.Bind(Config, Logger);
        Logger.LogInfo($"{Name} {Version}: patches, Slickster, wall flags, mags, wraps, armbands, gun and helmet displays, gun case, patch case, FTHS, ARC V2, JPC, AirLite, Avon mask, Grunts, Zyn tins, vape and RC bomb car started");
    }
}

/// <summary>What the parts read from BepInEx's plugin info: only where the DLL sits.</summary>
public sealed class PartInfo
{
    public PartInfo(string location) { Location = location; }
    public string Location { get; }
}
#endif
