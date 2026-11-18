using SPTarkov.Server.Core.Models.Spt.Mod;

namespace AvonMask.Server;

/// <summary>Required by the mod loader. Keep Version in step with AvonMask.Server.csproj.</summary>
#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.avonmask.server";

    public string Name { get; init; } = "Avon Gas Mask";

    public string Author { get; init; } = "Widowz";

    public List<string>? Contributors { get; init; }

    public SemanticVersioning.Version Version { get; init; } = new("0.4.1");

    // Minimum must match the referenced SPT package (4.1.3) and covers 4.1.6.
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");

    public List<string>? Incompatibilities { get; init; }

    // The buy-back offer goes on The Jackal (WWC keeps the guid local.jackal).
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new() { ["local.jackal"] = new("^1.0.0") };

    public string? Url { get; init; }

    // The standalone build's own licence. Built into WWC (its public build), WWC's metadata and LICENSE apply instead.
    public string License { get; init; } = "Private";

    public bool HasPrepatcher { get; init; } = false;
}
