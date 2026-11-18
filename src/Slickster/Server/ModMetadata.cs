using SPTarkov.Server.Core.Models.Spt.Mod;

namespace Slickster.Server;

/// <summary>
/// Required by the mod loader. Keep ModGuid / Name / Author / Version in step with Template.Server.csproj
/// (ModAuthor, AssemblyName, Version): the zip name and the mods folder name are built from them.
/// </summary>
#if !WWC   // built into WWC, the mod's metadata is WWC's own (mods/WWC/Server/Wwc.cs)
public record ModMetadata : IModMetadata
{
    // Reverse-domain style, unique. Private ports here use "local.<mod>.server".
    public string ModGuid { get; init; } = "local.slickster.server";

    public string Name { get; init; } = "Slickster Plate Carriers";

    public string Author { get; init; } = "Widowz";

    public List<string>? Contributors { get; init; }

    public SemanticVersioning.Version Version { get; init; } = new("0.2.2");

    // Minimum must match the referenced SPT package (4.1.3) and covers 4.1.6.
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");

    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new() { ["local.jackal"] = new("^1.0.0") };

    public string? Url { get; init; }

    // The standalone build's own licence. Built into WWC (its public build), WWC's metadata and LICENSE apply instead.
    public string License { get; init; } = "Private";

    public bool HasPrepatcher { get; init; } = false;
}
#endif




