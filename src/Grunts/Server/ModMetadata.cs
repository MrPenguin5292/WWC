using SPTarkov.Server.Core.Models.Spt.Mod;

namespace Grunts.Server;

/// <summary>Keep in step with Grunts.Server.csproj (ModAuthor, AssemblyName, Version).</summary>
#if WWC   // built into WWC: WWC's own metadata is the mod's (mods/WWC/Server/Wwc.cs)
public record ModMetadata
#else
public record ModMetadata : IModMetadata
#endif
{
    public string ModGuid { get; init; } = "local.grunts.server";

    public string Name { get; init; } = "Grunts";

    public string Author { get; init; } = "Widowz";

    public List<string>? Contributors { get; init; }

    public SemanticVersioning.Version Version { get; init; } = new("0.1.3");

    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");

    public List<string>? Incompatibilities { get; init; }

    // The jobs are The Jackal's (WWC, guid local.jackal).
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new() { ["local.jackal"] = new("^1.0.0") };

    public string? Url { get; init; }

    // The standalone build's own licence. Built into WWC (its public build), WWC's metadata and LICENSE apply instead.
    public string License { get; init; } = "Private";

    public bool HasPrepatcher { get; init; } = false;
}

internal static class ModInfo
{
    public static readonly string Name = new ModMetadata().Name;
}
