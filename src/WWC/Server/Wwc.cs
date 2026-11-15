using System.Reflection;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Loaders;
using SPTarkov.Server.Core.Models.Spt.Bundles;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Services.Server;
using SPTarkov.Server.Core.Utils;
using Path = System.IO.Path;

namespace WWC.Server;

/// <summary>
/// WWC - Wet Works Company. Keeps The Jackal's guid (local.jackal) and a 1.x version, so every mod that needs The Jackal
/// (ModDependencies local.jackal ^1.0.0) loads unchanged. The parts keep their ids, load stages and settings.
/// </summary>
public sealed record WwcMetadata : IModMetadata
{
#if WWC_FORGE
    // Forge build: the Forge wants one GUID shared by the server mod, the client plugin and the Forge entry, and a plain name.
    public string ModGuid { get; init; } = "com.widowz.wwc";
    public string Name { get; init; } = "WWC";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("2.0.0");
#else
    public string ModGuid { get; init; } = "local.jackal";
    public string Name { get; init; } = "WWC - Wet Works Company";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.2.2");
#endif
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.6");
    public bool HasPrepatcher { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
#if WWC_FORGE
    public string License { get; init; } = "MIT";
#else
    public string License { get; init; } = "Private; ported assets and the supplied portraits stay with their owners";
#endif
}

/// <summary>Where each part keeps its files: a folder of its own inside this mod, laid out as that part's old mod folder was.</summary>
public static class Parts
{
    public const string Jackal = "Jackal", Origin = "Origin", Tees = "Tees", Flags = "Flags", Patches = "Patches", Slickster = "Slickster", Jobs = "Jobs";
    public const string ServerFolder = "Widowz-WWC.Server";
    public const string ClientFolder = "WWC";
    public const string ClientDll = "WWC.Client.dll";

    /// <summary>The parts whose bundles the server hands to the game (each has its own bundles.json and bundles folder).</summary>
    public static readonly string[] WithBundles = [Tees, Flags];

    public static string Root => Path.GetDirectoryName(typeof(Parts).Assembly.Location)!;
    public static string Dir(string part) => Path.Combine(Root, part);

    /// <summary>The game folder: SPT_Runtime's parent.</summary>
    public static string GameRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    public static string ClientDir => Path.Combine(GameRoot, "BepInEx", "plugins", ClientFolder);
    public static bool ClientPresent => File.Exists(Path.Combine(ClientDir, ClientDll));

    /// <summary>The separate mods WWC replaces. Any of them still installed would load the same items, quests or trader twice.</summary>
    public static readonly string[] ReplacedServerFolders =
        ["TheJackal", "Widowz-JackalOrigin.Server", "Widowz-JackalTee.Server", "Widowz-WallFlag.Server", "Widowz-JackalPatches.Server", "Slickster", "Widowz-WWCJobs.Server"];
    public static readonly string[] ReplacedPluginFolders = ["JackalPatches", "WallFlag", "Slickster"];
}

/// <summary>Runs first: says plainly when one of the old separate mods is still installed beside WWC.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public sealed class OldModGuard(ISptLogger<OldModGuard> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var mods = Path.GetDirectoryName(Parts.Root)!;
        var plugins = Path.Combine(Parts.GameRoot, "BepInEx", "plugins");
        var left = Parts.ReplacedServerFolders.Where(f => Directory.Exists(Path.Combine(mods, f))).Select(f => @"SPT_Runtime\user\mods\" + f)
            .Concat(Parts.ReplacedPluginFolders.Where(f => Directory.Exists(Path.Combine(plugins, f))).Select(f => @"BepInEx\plugins\" + f)).ToList();
        if (left.Count > 0)
            logger.Error($"[WWC] These old mods are still installed and are part of WWC now, so their items and quests load twice: {string.Join(", ", left)}. Remove them (WWC's Install.ps1 does this).");
        logger.Info($"[WWC] WWC - Wet Works Company {typeof(WwcMetadata).Assembly.GetName().Version}: The Jackal, Origin, Tees, Flags, Patches, Slickster"
#if WWC_JOBS
            + ", Jobs"
#endif
            + (Parts.ClientPresent ? "" : $"; client plugin missing ({@"BepInEx\plugins\" + Parts.ClientFolder + @"\" + Parts.ClientDll}), so nothing that needs it is sold or placed"));
        return Task.CompletedTask;
    }
}

/// <summary>
/// The tees and flags each keep their own bundles.json and bundles folder, as their kits write them. SPT only reads a bundles.json at the
/// root of a mod, so this registers those bundles the same way it would (same keys, path relative to SPT_Runtime, CRC from SPT's own cache).
/// Runs after SPT has loaded every mod's bundles and before the game can ask for them.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.GameCallbacks + 50)]
public sealed class PartBundles(ISptLogger<PartBundles> logger, BundleLoader bundles, BundleHashCacheService hashes, JsonUtil json) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var cwd = Directory.GetCurrentDirectory();
        int ok = 0, missing = 0, invalid = 0;
        foreach (var part in Parts.WithBundles)
        {
            var dir = Parts.Dir(part);
            var manifestPath = Path.Combine(dir, "bundles.json");
            if (!File.Exists(manifestPath)) continue;
            BundleManifest? manifest;
            try { manifest = await json.DeserializeFromFileAsync<BundleManifest>(manifestPath, cancellationToken); }
            catch (Exception ex) { logger.Error($"[WWC] {part}\\bundles.json could not be read ({ex.Message}); its items keep the donor look"); continue; }
            if (manifest?.Manifest is null) { logger.Warning($"[WWC] {part}\\bundles.json lists no bundles"); continue; }
            var relative = Path.GetRelativePath(cwd, dir).Replace('\\', '/');
            foreach (var entry in manifest.Manifest)
            {
                if (string.IsNullOrWhiteSpace(entry.Key)) continue;
                var local = Path.Join(relative, "bundles", entry.Key).Replace('\\', '/');
                if (!File.Exists(local)) { logger.Error($"[WWC] {part}: bundle {entry.Key} is listed but not on disk"); missing++; continue; }
                var hash = await hashes.GetOrCalculateHashAsync(local, cancellationToken);
                if (hash is null) { logger.Error($"[WWC] {part}: {entry.Key} is not a Unity asset bundle"); invalid++; continue; }
                if (bundles.GetBundle(entry.Key) is not null) { logger.Error($"[WWC] {part}: bundle {entry.Key} is already served by another mod; kept that one"); invalid++; continue; }
                bundles.AddBundle(entry.Key, new BundleInfo { ModPath = relative, Bundle = entry, Crc = hash.Crc, Size = hash.Size, ModifiedUtcTicks = hash.ModifiedUtcTicks });
                ok++;
            }
        }
        await hashes.WriteCacheAsync(cancellationToken);
        logger.Info($"[WWC] part bundles: {ok} served, {missing} missing, {invalid} skipped");
    }
}
