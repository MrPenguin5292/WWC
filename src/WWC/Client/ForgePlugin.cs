#if WWC_FORGE
using BepInEx;
using UnityEngine;

namespace WWC.Client;

/// <summary>
/// The one BepInEx plugin of the Forge build (the Forge wants a single plugin whose GUID, name and version match the server mod).
/// It adds the three client parts as components, in the order BepInEx loaded them as separate plugins (by GUID): patches, Slickster,
/// wall flags. Their Harmony priorities (the patches' item hook runs last) do not depend on this order.
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public sealed class ForgePlugin : BaseUnityPlugin
{
    public const string Guid = "com.mrpenguin5292.wwc";
    public const string Name = "MrPenguin5292-WWC";
    public const string Version = "2.0.0";

    /// <summary>The DLL's location, which the parts use to find their files next to it.</summary>
    internal static PartInfo Part { get; private set; } = new(string.Empty);

    private void Awake()
    {
        Part = new PartInfo(Info.Location);
        gameObject.AddComponent<JackalPatches.Client.Plugin>();
        gameObject.AddComponent<Slickster.Client.Plugin>();
        gameObject.AddComponent<WallFlag.Client.Plugin>();
        Logger.LogInfo($"{Name} {Version}: patches, Slickster and wall flags started");
    }
}

/// <summary>What the parts read from BepInEx's plugin info: only where the DLL sits.</summary>
public sealed class PartInfo
{
    public PartInfo(string location) { Location = location; }
    public string Location { get; }
}
#endif
