using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Path = System.IO.Path;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace JackalPatches.Server;

#if !WWC   // built into WWC, the mod's metadata is WWC's own (mods/WWC/Server/Wwc.cs)
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "local.jackalpatches.server";
    public string Name { get; init; } = "Jackal Patches";
    public string Author { get; init; } = "Widowz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.9.3");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.3");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new() { ["local.jackal"] = new("^1.0.0") };
    public string? Url { get; init; }
    public string License { get; init; } = "Private";
    public bool HasPrepatcher { get; init; } = false;
}
#endif

/// <summary>Ids come from fixed text, so a rebuild never changes a template a profile may hold.</summary>
internal static class Ids
{
    public static MongoId Of(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("jackalpatch|" + key))).ToLowerInvariant()[..24]);
    public static readonly MongoId Wall = Of("wall"), WallOffer = Of("offer|wall");
    public const string Jackal = "672b4a010000000000000001";
    public const string Rouble = "5449016a4bdc2d6f028b456f";
    public const string HideoutCustomization = "673c7b00cbf4b984b5099181";
    public const string SlicksterRig = "d2bdedaf3de0b53aa9e7a081";   // the Slickster carrier: its chest and dock slots take patches
    public const string PatchDonor = "5a16b8a9fcdbcb00165aa6ca";   // Norotos TATM, same small mount the Slickster patches clone
    public const string WallDonor = "590c60fc86f77412b13fddcf";    // docs case: a real container, so the wall has item slots
    public const string WallPrefab = "assets/content/items/barter/item_barter_flyers/item_barter_flyers_alex_green.bundle";   // a stock wall poster's model
    public static readonly string[] SlicksterPatches = ["aggressive_4", "curve2", "hoot2", "totn"];
    public static MongoId Slickster(string key) => new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("slickster|patch|" + key))).ToLowerInvariant()[..24]);
}

/// <summary>Everything in config.json. A value left out keeps the default written here, and <see cref="Sane"/> pulls anything out of range back in, so an edit can't break a profile.</summary>
public record PatchConfig
{
    [JsonPropertyName("modEnabled")] public bool ModEnabled { get; set; } = true;
    [JsonPropertyName("jackal")] public JackalSettings Jackal { get; set; } = new();
    [JsonPropertyName("fleaMarket")] public FleaSettings Flea { get; set; } = new();
    [JsonPropertyName("patchTypes")] public PatchTypeTable Types { get; set; } = new();
    [JsonPropertyName("items")] public ItemSettings Items { get; set; } = new();
    [JsonPropertyName("raidSpawns")] public RaidSettings Raid { get; set; } = new();
    [JsonPropertyName("rigs")] public RigSettings Rigs { get; set; } = new();

    public PatchConfig Sane()
    {
        static double Money(double v) => Math.Clamp(v, 1, 1_000_000_000);
        static double Weight(double v) => Math.Clamp(v, 0, 1000);
        static int Count(int v) => Math.Clamp(v, 1, 9_999_999);
        static int Level(int v) => Math.Clamp(v, 1, 4);
        static int Cells(int v) => Math.Clamp(v, 1, 20);
        Jackal.PatchPriceRoubles = Money(Jackal.PatchPriceRoubles); Jackal.WallPriceRoubles = Money(Jackal.WallPriceRoubles);
        Jackal.PatchStock = Count(Jackal.PatchStock); Jackal.PatchBuyLimit = Count(Jackal.PatchBuyLimit); Jackal.WallStock = Count(Jackal.WallStock); Jackal.WallBuyLimit = Count(Jackal.WallBuyLimit);
        Jackal.PatchLoyaltyLevel = Level(Jackal.PatchLoyaltyLevel); Jackal.WallLoyaltyLevel = Level(Jackal.WallLoyaltyLevel);
        Flea.PatchTraderValueRoubles = Money(Flea.PatchTraderValueRoubles); Flea.WallFleaPriceRoubles = Money(Flea.WallFleaPriceRoubles); Flea.WallTraderValueRoubles = Money(Flea.WallTraderValueRoubles);
        foreach (var t in Types.All) { t.FleaPriceRoubles = Money(t.FleaPriceRoubles); t.RaidWeight = Weight(t.RaidWeight); }
        Items.PatchWeightKg = Weight(Items.PatchWeightKg); Items.WallWeightKg = Weight(Items.WallWeightKg);
        Items.PatchWidth = Cells(Items.PatchWidth); Items.PatchHeight = Cells(Items.PatchHeight); Items.WallWidth = Cells(Items.WallWidth); Items.WallHeight = Cells(Items.WallHeight);
        Items.BoardColumns = Cells(Items.BoardColumns); Items.BoardRows = Cells(Items.BoardRows);
        Raid.RarityBasePriceRoubles = Money(Raid.RarityBasePriceRoubles);
        Raid.PatchesPerRaid = Weight(Raid.PatchesPerRaid); Raid.MaxShareOfASpot = Math.Clamp(Raid.MaxShareOfASpot, 0, 0.95); Raid.SlicksterWeight = Weight(Raid.SlicksterWeight);
        Raid.Maps.Clamp(); Raid.Spots.Clamp();
        return this;
    }
}

public record RigSettings
{
    [JsonPropertyName("fitOnSlicksterCarrier")] public bool FitOnSlicksterCarrier { get; set; } = true;
}

public record JackalSettings
{
    [JsonPropertyName("sellPatches")] public bool SellPatches { get; set; }
    [JsonPropertyName("patchPriceRoubles")] public double PatchPriceRoubles { get; set; } = 1_500;
    [JsonPropertyName("patchStock")] public int PatchStock { get; set; } = 20;
    [JsonPropertyName("patchBuyLimit")] public int PatchBuyLimit { get; set; } = 20;
    [JsonPropertyName("patchLoyaltyLevel")] public int PatchLoyaltyLevel { get; set; } = 1;
    [JsonPropertyName("sellWall")] public bool SellWall { get; set; } = true;
    [JsonPropertyName("wallPriceRoubles")] public double WallPriceRoubles { get; set; } = 45_000;
    [JsonPropertyName("wallStock")] public int WallStock { get; set; } = 3;
    [JsonPropertyName("wallBuyLimit")] public int WallBuyLimit { get; set; } = 1;
    [JsonPropertyName("wallLoyaltyLevel")] public int WallLoyaltyLevel { get; set; } = 1;
}

public record FleaSettings
{
    [JsonPropertyName("sellPatches")] public bool SellPatches { get; set; } = true;
    [JsonPropertyName("patchTraderValueRoubles")] public double PatchTraderValueRoubles { get; set; } = 20_000;
    [JsonPropertyName("sellWall")] public bool SellWall { get; set; }
    [JsonPropertyName("wallFleaPriceRoubles")] public double WallFleaPriceRoubles { get; set; } = 60_000;
    [JsonPropertyName("wallTraderValueRoubles")] public double WallTraderValueRoubles { get; set; } = 30_000;
}

public record PatchTypeSettings
{
    [JsonPropertyName("fleaPriceRoubles")] public double FleaPriceRoubles { get; set; } = 150_000;
    [JsonPropertyName("raidWeight")] public double RaidWeight { get; set; } = 1;
}

public record PatchTypeTable
{
    [JsonPropertyName("embroidered")] public PatchTypeSettings Embroidered { get; set; } = new();
    [JsonPropertyName("lasercut")] public PatchTypeSettings Lasercut { get; set; } = new();
    [JsonPropertyName("woven")] public PatchTypeSettings Woven { get; set; } = new() { FleaPriceRoubles = 125_000 };
    [JsonPropertyName("printed")] public PatchTypeSettings Printed { get; set; } = new() { FleaPriceRoubles = 100_000 };
    [JsonPropertyName("pvc")] public PatchTypeSettings Pvc { get; set; } = new() { FleaPriceRoubles = 175_000 };
    [JsonPropertyName("chenille")] public PatchTypeSettings Chenille { get; set; } = new() { FleaPriceRoubles = 175_000 };
    [JsonPropertyName("ir")] public PatchTypeSettings Ir { get; set; } = new() { FleaPriceRoubles = 300_000 };
    [JsonPropertyName("puff")] public PatchTypeSettings Puff { get; set; } = new() { FleaPriceRoubles = 200_000 };
    [JsonPropertyName("sublimated")] public PatchTypeSettings Sublimated { get; set; } = new() { FleaPriceRoubles = 110_000 };
    [JsonPropertyName("applique")] public PatchTypeSettings Applique { get; set; } = new() { FleaPriceRoubles = 160_000 };
    [JsonPropertyName("leather")] public PatchTypeSettings Leather { get; set; } = new() { FleaPriceRoubles = 225_000 };
    [JsonPropertyName("sequin")] public PatchTypeSettings Sequin { get; set; } = new() { FleaPriceRoubles = 250_000 };

    [JsonIgnore] public IEnumerable<PatchTypeSettings> All => [Embroidered, Lasercut, Woven, Printed, Pvc, Chenille, Ir, Puff, Sublimated, Applique, Leather, Sequin];

    /// <summary>A patch carrying IR film anywhere counts as an IR patch, whatever it's stitched from.</summary>
    public PatchTypeSettings For(PatchEntry e) => e.Ir ? Ir : e.Style.ToLowerInvariant() switch
    {
        "lasercut" => Lasercut, "woven" => Woven, "printed" => Printed, "pvc" => Pvc, "chenille" => Chenille, "ir" => Ir,
        "puff" => Puff, "sublimated" => Sublimated, "applique" => Applique, "leather" => Leather, "sequin" => Sequin, _ => Embroidered,
    };
}

public record ItemSettings
{
    [JsonPropertyName("patchWeightKg")] public double PatchWeightKg { get; set; } = 0.02;
    [JsonPropertyName("patchWidth")] public int PatchWidth { get; set; } = 1;
    [JsonPropertyName("patchHeight")] public int PatchHeight { get; set; } = 1;
    [JsonPropertyName("wallWeightKg")] public double WallWeightKg { get; set; } = 3.5;
    [JsonPropertyName("wallWidth")] public int WallWidth { get; set; } = 2;
    [JsonPropertyName("wallHeight")] public int WallHeight { get; set; } = 2;
    [JsonPropertyName("boardColumns")] public int BoardColumns { get; set; } = 10;
    [JsonPropertyName("boardRows")] public int BoardRows { get; set; } = 8;
}

public record RaidSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("patchesPerRaid")] public double PatchesPerRaid { get; set; } = 0.1;
    [JsonPropertyName("rarityFollowsFleaPrice")] public bool RarityFollowsFleaPrice { get; set; } = true;
    [JsonPropertyName("rarityBasePriceRoubles")] public double RarityBasePriceRoubles { get; set; } = 150_000;
    [JsonPropertyName("maxShareOfASpot")] public double MaxShareOfASpot { get; set; } = 0.6;
    [JsonPropertyName("irOnlyInMilitarySpots")] public bool IrOnlyInMilitarySpots { get; set; } = true;
    [JsonPropertyName("includeSlicksterPatches")] public bool IncludeSlicksterPatches { get; set; } = true;
    [JsonPropertyName("slicksterWeight")] public double SlicksterWeight { get; set; } = 1;
    [JsonPropertyName("mapWeights")] public MapWeights Maps { get; set; } = new();
    [JsonPropertyName("spotWeights")] public SpotWeights Spots { get; set; } = new();
}

public record MapWeights
{
    [JsonPropertyName("customs")] public double Customs { get; set; } = 0.8;
    [JsonPropertyName("factoryDay")] public double FactoryDay { get; set; } = 0.5;
    [JsonPropertyName("factoryNight")] public double FactoryNight { get; set; } = 0.6;
    [JsonPropertyName("groundZero")] public double GroundZero { get; set; } = 0.8;
    [JsonPropertyName("groundZeroHigh")] public double GroundZeroHigh { get; set; } = 1.0;
    [JsonPropertyName("interchange")] public double Interchange { get; set; } = 0.6;
    [JsonPropertyName("labs")] public double Labs { get; set; } = 1.2;
    [JsonPropertyName("labyrinth")] public double Labyrinth { get; set; } = 0.8;
    [JsonPropertyName("lighthouse")] public double Lighthouse { get; set; } = 1.5;
    [JsonPropertyName("reserve")] public double Reserve { get; set; } = 1.8;
    [JsonPropertyName("shoreline")] public double Shoreline { get; set; } = 0.8;
    [JsonPropertyName("streets")] public double Streets { get; set; } = 1.0;
    [JsonPropertyName("woods")] public double Woods { get; set; } = 1.2;

    public void Clamp()
    {
        static double C(double v) => Math.Clamp(v, 0, 1000);
        Customs = C(Customs); FactoryDay = C(FactoryDay); FactoryNight = C(FactoryNight); GroundZero = C(GroundZero); GroundZeroHigh = C(GroundZeroHigh); Interchange = C(Interchange);
        Labs = C(Labs); Labyrinth = C(Labyrinth); Lighthouse = C(Lighthouse); Reserve = C(Reserve); Shoreline = C(Shoreline); Streets = C(Streets); Woods = C(Woods);
    }

    /// <summary>Null for a map the mod doesn't place patches on, such as the hideout.</summary>
    public double? For(string mapId) => mapId.ToLowerInvariant() switch
    {
        "bigmap" => Customs, "factory4_day" => FactoryDay, "factory4_night" => FactoryNight, "sandbox" => GroundZero, "sandbox_high" => GroundZeroHigh, "interchange" => Interchange,
        "laboratory" => Labs, "labyrinth" => Labyrinth, "lighthouse" => Lighthouse, "rezervbase" => Reserve, "shoreline" => Shoreline, "tarkovstreets" => Streets, "woods" => Woods,
        _ => null,
    };
}

public record SpotWeights
{
    [JsonPropertyName("ammo")] public double Ammo { get; set; } = 1;
    [JsonPropertyName("weaponParts")] public double WeaponParts { get; set; } = 1;
    [JsonPropertyName("military")] public double Military { get; set; } = 1;
    [JsonPropertyName("gear")] public double Gear { get; set; } = 1;
    [JsonPropertyName("clothes")] public double Clothes { get; set; } = 1;

    public void Clamp()
    {
        static double C(double v) => Math.Clamp(v, 0, 1000);
        Ammo = C(Ammo); WeaponParts = C(WeaponParts); Military = C(Military); Gear = C(Gear); Clothes = C(Clothes);
    }
}

public record PatchEntry
{
    [JsonPropertyName("n")] public int N { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("style")] public string Style { get; set; } = "embroidered";
    [JsonPropertyName("ir")] public bool Ir { get; set; }
    /// <summary>Out of circulation: the item stays registered so anything already owned keeps working, but it's no longer sold or placed in raids.</summary>
    [JsonPropertyName("retired")] public bool Retired { get; set; }
}

internal static class PatchText
{
    public static string Describe(string style) => (style switch
    {
        "woven" => "A thin, finely woven morale patch",
        "printed" => "A screen-printed morale patch on twill",
        "pvc" => "A moulded rubber PVC morale patch",
        "chenille" => "A tufted chenille morale patch",
        "ir" => "A subdued tan IR-style morale patch",
        "lasercut" => "A morale patch of layered Cordura, its design laser-cut through to the colours underneath",
        "puff" => "A raised 3D embroidered morale patch, stitched over foam",
        "sublimated" => "A full-colour dye-sublimated morale patch",
        "applique" => "A morale patch of cut cloth pieces sewn onto a base",
        "leather" => "A full-grain leather morale patch with pressed art and a stitched border",
        "sequin" => "A morale patch covered in small glittering sequins",
        _ => "An embroidered morale patch with a merrowed edge",
    }) + " with a hook-velcro back. Hang it up on the patch wall.";
}

internal static class Shared
{
    public static List<PatchEntry> Read<T>(ModHelper helper, string dir, ISptLogger<T> logger)
    {
        if (!File.Exists(Path.Combine(dir, "patches.json"))) return [];
        try
        {
            return (helper.GetJsonDataFromFile<List<PatchEntry>>(dir, "patches.json") ?? []).Where(e => e.N >= 1).OrderBy(e => e.N).ToList();
        }
        catch (Exception ex)
        {
            logger.Warning($"[JackalPatches] patches.json could not be read ({ex.Message}); no patches registered");
            return [];
        }
    }

    // People edit this file by hand, so notes after // and a stray comma at the end of a list are fine.
    private static readonly JsonSerializerOptions Forgiving = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };
    private static PatchConfig? loaded;

    public static PatchConfig Config<T>(string dir, ISptLogger<T> logger)
    {
        if (loaded is not null) return loaded;
        var path = Path.Combine(dir, "config.json");
        try { loaded = (File.Exists(path) ? JsonSerializer.Deserialize<PatchConfig>(File.ReadAllText(path), Forgiving) : null) ?? new PatchConfig(); }
        catch (JsonException ex) { logger.Error($"[JackalPatches] config.json has a mistake near line {ex.LineNumber + 1}, so the mod is using its normal values until that's fixed. {ex.Message}"); loaded = new PatchConfig(); }
        catch (Exception ex) { logger.Error($"[JackalPatches] config.json could not be read ({ex.Message}), so the mod is using its normal values"); loaded = new PatchConfig(); }
        return loaded.Sane();
    }

    /// <summary>True when the client plugin is present, so a half-installed mod never sells items that have no model.</summary>
    public static bool ClientPresent(string serverModDir)
    {
#if WWC
        return WWC.Server.Parts.ClientPresent;
#else
        var gameRoot = Path.GetFullPath(Path.Combine(serverModDir, "..", "..", "..", ".."));
        return File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "JackalPatches", "JackalPatches.Client.dll"));
#endif
    }
}

/// <summary>Stage 1: templates. Always registered so a profile that holds a patch or the wall still loads when the client is gone.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 30)]
public class Templates(ISptLogger<Templates> logger, ModHelper modHelper, CustomItemService customItems, TemplateTable templates, LocaleTable locales, ItemConfig itemConfig,
    RagfairConfig ragfairConfig, PmcConfig pmcConfig, ScavCaseConfig scavCaseConfig, HideoutConfig hideoutConfig, AirdropConfig airdropConfig, TraderConfig traderConfig) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        if (!templates.Items.TryGetValue(Ids.PatchDonor, out var patchDonor) || !templates.Items.TryGetValue(Ids.WallDonor, out var wallDonor))
        {
            logger.Error("[JackalPatches] donor items not found; nothing registered");
            return Task.CompletedTask;
        }
        var cfg = Shared.Config(dir, logger);
        // Never lists anything on the flea with the client missing, because the buyer would only get a donor model.
        var clientThere = cfg.ModEnabled && Shared.ClientPresent(dir);
        var strings = new Dictionary<string, string>();
        var handbook = templates.Handbook.Items;
        var patchParent = handbook.FirstOrDefault(h => h.Id == Ids.PatchDonor)?.ParentId;
        var wallParent = handbook.FirstOrDefault(h => h.Id == Ids.WallDonor)?.ParentId;

        var added = 0;
        var patchIds = new HashSet<MongoId>(Ids.SlicksterPatches.Select(Ids.Slickster));
        // Only this mod's own items go on the blacklists below; the Slickster patches belong to that mod.
        var ownIds = new List<MongoId>();
        var offFlea = new List<MongoId>();
        foreach (var e in Shared.Read(modHelper, dir, logger))
        {
            var id = Ids.Of($"patch|{e.N}");
            var onFlea = clientThere && cfg.Flea.SellPatches && !e.Retired;
            var name = string.IsNullOrWhiteSpace(e.Name) ? $"Patch {e.N}" : e.Name.Trim();
            var fleaPrice = cfg.Types.For(e).FleaPriceRoubles;
            var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
            {
                NewId = id,
                ItemTplToClone = Ids.PatchDonor,
                ParentId = patchDonor.Parent,
                NewItemName = $"jackalpatch_{e.N}",
                OverrideProperties = new TemplateItemProperties
                {
                    Slots = [], Grids = [], ConflictingItems = [],
                    Width = cfg.Items.PatchWidth, Height = cfg.Items.PatchHeight, Weight = cfg.Items.PatchWeightKg,
                    ExaminedByDefault = true, CanSellOnRagfair = onFlea, CanRequireOnRagfair = false,
                },
                HandbookParentId = patchParent,
                // SPT caps a weapon mod's flea price at six times its handbook price, and patches clone a mount. Staying within a fifth keeps the price asked for.
                HandbookPriceRoubles = Math.Max(cfg.Flea.PatchTraderValueRoubles, Math.Ceiling(fleaPrice / 5.0)),
                FleaPriceRoubles = fleaPrice,
                AddToHandbook = patchParent is not null,
                AddToFleaPriceDb = true,
                AddToWeaponShelf = true,
                Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = name, ShortName = name, Description = PatchText.Describe(e.Style) } },
            }, Assembly.GetExecutingAssembly());
            if (!result.Success) { logger.Error($"[JackalPatches] {name}: {string.Join("; ", result.Errors)}"); continue; }
            strings[$"{id} Name"] = name;
            strings[$"{id} ShortName"] = name;
            strings[$"{id} Description"] = PatchText.Describe(e.Style);
            patchIds.Add(id);
            ownIds.Add(id);
            if (!onFlea) offFlea.Add(id);
            added++;
        }

        var wall = customItems.CreateItemFromClone(new NewItemFromCloneDetails
        {
            NewId = Ids.Wall,
            ItemTplToClone = Ids.WallDonor,
            ParentId = wallDonor.Parent,
            NewItemName = "jackalpatch_wall",
            OverrideProperties = new TemplateItemProperties
            {
                Width = cfg.Items.WallWidth, Height = cfg.Items.WallHeight, Weight = cfg.Items.WallWeightKg,
                ExaminedByDefault = true, CanSellOnRagfair = clientThere && cfg.Flea.SellWall, CanRequireOnRagfair = false,
                Prefab = new Prefab { Path = Ids.WallPrefab, Rcid = "" },
            },
            HandbookParentId = wallParent,
            HandbookPriceRoubles = cfg.Flea.WallTraderValueRoubles,
            FleaPriceRoubles = cfg.Flea.WallFleaPriceRoubles,
            AddToHandbook = wallParent is not null,
            AddToFleaPriceDb = true,
            AddToWeaponShelf = true,
            Locales = new Dictionary<string, LocaleDetails> { ["en"] = new() { Name = "Patch wall", ShortName = "Patch wall", Description = "A velcro board for a wall poster spot in your hideout. Walk up to it and open its slots to hang your patches." } },
        }, Assembly.GetExecutingAssembly());
        if (!wall.Success) logger.Error($"[JackalPatches] patch wall: {string.Join("; ", wall.Errors)}");
        else
        {
            strings[$"{Ids.Wall} Name"] = "Patch wall";
            strings[$"{Ids.Wall} ShortName"] = "Patch wall";
            strings[$"{Ids.Wall} Description"] = "A velcro board for a wall poster spot in your hideout. Walk up to it and open its slots to hang your patches.";
            ownIds.Add(Ids.Wall);
            if (!(clientThere && cfg.Flea.SellWall)) offFlea.Add(Ids.Wall);
            ConfigureWallSlots(patchIds, cfg.Items);
            if (cfg.Rigs.FitOnSlicksterCarrier) AddToRigSlots(patchIds);
            AddToPosterSpots();
        }

        foreach (var (lang, lazy) in locales.Global)
        {
            var overwrite = lang.Equals("en", StringComparison.OrdinalIgnoreCase);
            lazy.AddTransformer(dict =>
            {
                if (dict is null) return dict;
                foreach (var (k, v) in strings)
                {
                    if (overwrite) dict[k] = v; else dict.TryAdd(k, v);
                }
                return dict;
            });
        }
        // Patches are collectibles: found in raids or bought on the flea, never from a loot table, Fence, a reward, the scav case, the cultist circle or an airdrop. The wall comes from The Jackal.
        itemConfig.RewardItemBlacklist.UnionWith(ownIds);
        ragfairConfig.Dynamic.Blacklist.Custom.UnionWith(offFlea);
        pmcConfig.GlobalLootBlacklist.AddRange(ownIds.Except(pmcConfig.GlobalLootBlacklist));
        scavCaseConfig.RewardItemBlacklist.UnionWith(ownIds);
        hideoutConfig.CultistCircle.RewardItemBlacklist.AddRange(ownIds.Except(hideoutConfig.CultistCircle.RewardItemBlacklist));
        traderConfig.Fence.Blacklist.UnionWith(ownIds);
        foreach (var drop in airdropConfig.Loot.Values) drop.ItemBlacklist.AddRange(ownIds.Except(drop.ItemBlacklist));
        logger.Success($"[JackalPatches] {added} patch(es) and the patch wall registered");
        return Task.CompletedTask;
    }

    /// <summary>The wall's slots: a grid that accepts only patches.</summary>
    private void ConfigureWallSlots(HashSet<MongoId> patchIds, ItemSettings size)
    {
        var grids = templates.Items[Ids.Wall].Properties?.Grids;
        var grid = grids?.FirstOrDefault();
        if (grid?.Properties is null) { logger.Error("[JackalPatches] patch wall has no grid to configure"); return; }
        grid.Properties.CellsH = size.BoardColumns;
        grid.Properties.CellsV = size.BoardRows;
        grid.Properties.Filters = [new GridFilter { Filter = patchIds, ExcludedFilter = [] }];
        logger.Info($"[JackalPatches] patch wall accepts {patchIds.Count} patch types");
    }

    /// <summary>Lets kit patches into the Slickster carrier's two patch slots. Only adds ids to its allow-lists, and does nothing if that mod isn't installed.</summary>
    private void AddToRigSlots(HashSet<MongoId> patchIds)
    {
        if (!templates.Items.TryGetValue(Ids.SlicksterRig, out var rig) || rig.Properties?.Slots is null) return;
        var added = 0;
        foreach (var slot in rig.Properties.Slots.Where(s => s.Name is "mod_equipment_000" or "mod_equipment_001"))
        {
            foreach (var filter in slot.Properties?.Filters ?? [])
            {
                if (filter.Filter is null) continue;
                foreach (var id in patchIds) filter.Filter.Add(id);
                added++;
            }
        }
        logger.Info($"[JackalPatches] patches can go in {added} patch slots on the Slickster carrier");
    }

    /// <summary>The wall may hang on any native poster spot. Only adds an id to each spot's allow-list, so other wall items keep working.</summary>
    private void AddToPosterSpots()
    {
        if (!templates.Items.TryGetValue(Ids.HideoutCustomization, out var stash) || stash.Properties?.Slots is null) return;
        var spots = 0;
        foreach (var slot in stash.Properties.Slots)
        {
            if (slot.Name is null || !slot.Name.StartsWith("Poster_", StringComparison.Ordinal)) continue;
            foreach (var f in slot.Properties?.Filters ?? [])
            {
                if (f.Filter is null) continue;
                f.Filter.Add(Ids.Wall);
                spots++;
            }
        }
        logger.Info($"[JackalPatches] patch wall allowed on {spots} poster spots");
    }
}

/// <summary>
/// Stage 1b: patches turn up as loose loot where a patch would actually be: among kit, ammo, weapon parts, military electronics and clothes, not in kitchens, medicine cabinets or
/// jewellery cases. Nothing is added to maps without the client plugin, so a loose patch never shows as its donor model. IR film patches stay in the military spots.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 40)]
public class LooseLoot(ISptLogger<LooseLoot> logger, ModHelper modHelper, LocationTable locations, TemplateTable templates, LocationConfig locationConfig) : IOnLoad
{
    private static readonly string[] Clothes = ["closh", "cloth"];
    private static readonly string[] Ammo = ["ammo"];
    private static readonly string[] WeaponParts = ["weapon", "loot_mod", "mods_all", "_mods", "mods"];
    private static readonly string[] MilitaryCrates = ["militar"];
    private static readonly string[] Gear = ["gear"];
    private static readonly string[] Military = ["militar", "ammo", "weapon", "loot_mod", "mods_all", "_mods", "gear"];
    private static readonly string[] Excluded = ["food", "med", "drink", "jewel", "jeverly", "money", "fuel", "garbage", "christmas", "tool", "cashier", "flyer", "drug"];

    private static bool Matches(string spot, string[] words) => words.Any(spot.Contains);

    /// <summary>How much a spot counts: the highest weight among the kinds it belongs to, or 0 for a spot where a patch wouldn't be.</summary>
    private static double SpotWeight(string spot, SpotWeights w)
    {
        if (Matches(spot, Excluded)) return 0;
        var best = 0.0;
        if (Matches(spot, Clothes)) best = Math.Max(best, w.Clothes);
        if (Matches(spot, Ammo)) best = Math.Max(best, w.Ammo);
        if (Matches(spot, WeaponParts)) best = Math.Max(best, w.WeaponParts);
        if (Matches(spot, MilitaryCrates)) best = Math.Max(best, w.Military);
        if (Matches(spot, Gear)) best = Math.Max(best, w.Gear);
        return best;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled || !cfg.Raid.Enabled || cfg.Raid.PatchesPerRaid <= 0) { logger.Info("[JackalPatches] raid spawns are off in config.json"); return Task.CompletedTask; }
        if (!Shared.ClientPresent(dir)) { logger.Warning("[JackalPatches] client files are not installed; no patches placed in raids"); return Task.CompletedTask; }
        // Weight is how often a patch turns up; Rarity scales it by flea price, so a patch twice the price is half as common and raising every price makes all of them scarcer.
        var common = new List<(MongoId Id, double Weight, double Rarity)>(); var military = new List<(MongoId Id, double Weight, double Rarity)>();
        foreach (var e in Shared.Read(modHelper, dir, logger))
        {
            var id = Ids.Of($"patch|{e.N}");
            if (e.Retired || !templates.Items.ContainsKey(id)) continue;
            var type = cfg.Types.For(e);
            if (type.RaidWeight <= 0) continue;
            var rarity = cfg.Raid.RarityFollowsFleaPrice ? cfg.Raid.RarityBasePriceRoubles / type.FleaPriceRoubles : 1.0;
            military.Add((id, type.RaidWeight, rarity));
            var isIr = e.Ir || e.Style.Equals("ir", StringComparison.OrdinalIgnoreCase);
            if (!isIr || !cfg.Raid.IrOnlyInMilitarySpots) common.Add((id, type.RaidWeight, rarity));
        }
        if (cfg.Raid.IncludeSlicksterPatches && cfg.Raid.SlicksterWeight > 0)
        {
            foreach (var key in Ids.SlicksterPatches)
            {
                var id = Ids.Slickster(key);
                if (templates.Items.ContainsKey(id)) { common.Add((id, cfg.Raid.SlicksterWeight, 1.0)); military.Add((id, cfg.Raid.SlicksterWeight, 1.0)); }
            }
        }
        if (military.Count == 0) { logger.Info("[JackalPatches] no patches to place in raids yet"); return Task.CompletedTask; }
        var maps = 0;
        foreach (var (_, location) in locations.GetDictionary())
        {
            if (location?.LooseLoot is null || location.Base?.Id is null) continue;
            var mapId = location.Base.Id;
            if (cfg.Raid.Maps.For(mapId) is not { } mapWeight || mapWeight <= 0) continue;
            maps++;
            var expected = cfg.Raid.PatchesPerRaid * mapWeight;
            location.LooseLoot.AddTransformer(loot =>
            {
                if (loot?.Spawnpoints is null) return loot;
                var points = loot.Spawnpoints.ToList();
                var chosen = points.Where(p => p.Template?.Id is not null && p.ItemDistribution is not null && p.Template.Items is not null)
                    .Select(p => (Point: p, Weight: SpotWeight(p.Template!.Id!.ToLowerInvariant(), cfg.Raid.Spots))).Where(c => c.Weight > 0).ToList();
                var totalChance = chosen.Sum(c => (double)(c.Point.Probability ?? 0f) * c.Weight);
                if (totalChance <= 0) return loot;
                // SPT picks about (map's spot count x its loot multiplier) spots, weighted by each spot's chance. Working from that real number keeps a raid's patch count close to the config on every map.
                var multiplier = Math.Max(1.0, locationConfig.LooseLootMultiplier.FirstOrDefault(m => m.Key.Equals(mapId, StringComparison.OrdinalIgnoreCase)).Value);
                var allChance = points.Sum(p => (double)(p.Probability ?? 0f));
                var picks = Math.Max(1.0, (loot.SpawnpointCount?.Mean ?? allChance) * multiplier);
                var baseShare = expected / (totalChance * Math.Min(picks / Math.Max(allChance, 1e-6), 1.0 / Math.Max(chosen.Max(c => (double)(c.Point.Probability ?? 0f)), 1e-6)));
                foreach (var (point, spotWeight) in chosen)
                {
                    var spot = point.Template!.Id!.ToLowerInvariant();
                    var pool = Matches(spot, Military) ? military : common;
                    if (pool.Count == 0) continue;
                    var poolWeight = pool.Sum(p => p.Weight);
                    var scaledWeight = pool.Sum(p => p.Weight * p.Rarity);
                    var share = Math.Clamp(baseShare * spotWeight * (scaledWeight / poolWeight), 0.0, cfg.Raid.MaxShareOfASpot);
                    if (share <= 0) continue;
                    var dist = point.ItemDistribution!.ToList();
                    var items = point.Template.Items!.ToList();
                    var weightOfAll = dist.Sum(d => (double)(d.RelativeProbability ?? 0f)) * share / (1.0 - share);
                    foreach (var (patch, patchWeight, rarity) in pool)
                    {
                        var weightEach = (float)(weightOfAll * patchWeight * rarity / scaledWeight);
                        if (weightEach <= 0) continue;
                        var key = $"jackalpatch-{patch}";
                        items.Add(new SptLootItem { Id = Ids.Of($"spot|{mapId}|{point.Template.Id}|{patch}"), Template = patch, Upd = new Upd { StackObjectsCount = 1 }, ComposedKey = key });
                        dist.Add(new LooseLootItemDistribution { ComposedKey = new ComposedKey { Key = key }, RelativeProbability = weightEach });
                    }
                    point.Template.Items = items;
                    point.ItemDistribution = dist;
                }
                loot.Spawnpoints = points;
                logger.Debug($"[JackalPatches] {mapId}: patches can spawn at {chosen.Count} spots, about {expected:0.0} per raid");
                return loot;
            });
        }
        logger.Success($"[JackalPatches] loose patches added to {maps} maps ({military.Count} patches, {common.Count} outside military spots)");
        return Task.CompletedTask;
    }
}

/// <summary>Stage 2: The Jackal's offers, after he registers at TraderRegistration + 100. Skipped when the client files are missing.</summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 130)]
public class Offers(ISptLogger<Offers> logger, ModHelper modHelper, TradersTable traders, TemplateTable templates) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var dir = ModFolder.Of(modHelper);
        var cfg = Shared.Config(dir, logger);
        if (!cfg.ModEnabled) { logger.Info("[JackalPatches] disabled in config.json, no offers"); return Task.CompletedTask; }
        if (!Shared.ClientPresent(dir)) { logger.Warning("[JackalPatches] client files are not installed; templates kept, offers skipped"); return Task.CompletedTask; }
        var jackal = traders.GetTrader(new MongoId(Ids.Jackal));
        if (jackal?.Assort is null) { logger.Warning("[JackalPatches] The Jackal not found; no offers"); return Task.CompletedTask; }
        var assort = jackal.Assort;
        assort.Items ??= [];
        assort.BarterScheme ??= new Dictionary<MongoId, List<List<BarterScheme>>>();
        assort.LoyalLevelItems ??= new Dictionary<MongoId, int>();

        void Offer(MongoId root, MongoId tpl, double price, int stock, int limit, int level)
        {
            if (!templates.Items.ContainsKey(tpl)) return;
            if (assort.Items.Any(i => i.Id == root)) return;
            assort.Items.Add(new Item
            {
                Id = root,
                Template = tpl,
                ParentId = "hideout",
                SlotId = "hideout",
                Upd = new Upd { UnlimitedCount = false, StackObjectsCount = stock, BuyRestrictionMax = limit, BuyRestrictionCurrent = 0 },
            });
            assort.BarterScheme[root] = [[new BarterScheme { Count = Math.Max(1, price), Template = new MongoId(Ids.Rouble) }]];
            assort.LoyalLevelItems[root] = level;
        }

        var seller = cfg.Jackal;
        if (seller.SellWall) Offer(Ids.WallOffer, Ids.Wall, seller.WallPriceRoubles, seller.WallStock, seller.WallBuyLimit, seller.WallLoyaltyLevel);
        var count = 0;
        if (seller.SellPatches)
        {
            foreach (var e in Shared.Read(modHelper, dir, logger).Where(e => !e.Retired))
            {
                Offer(Ids.Of($"offer|patch|{e.N}"), Ids.Of($"patch|{e.N}"), seller.PatchPriceRoubles, seller.PatchStock, seller.PatchBuyLimit, seller.PatchLoyaltyLevel);
                count++;
            }
        }
        logger.Success($"[JackalPatches] The Jackal sells {(seller.SellWall ? $"the patch wall ({seller.WallPriceRoubles:0} RUB)" : "no patch wall")} and {count} patch(es)");
        return Task.CompletedTask;
    }
}
